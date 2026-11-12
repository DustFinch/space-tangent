// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public static partial class SceneLink
{
    /// <summary>Exports one perspective observer in the legacy scene link format.</summary>
    public static string WriteLegacy(ObserverView observer, SimulationWorld world, DeltaTModel deltaT)
    {
        if (observer.Navigation is not { } pose)
            throw new InvalidDataException("Open a perspective camera before exporting a legacy scene link.");
        var snapshot = world.Graph.Evaluate(observer.Epoch);
        NavigationPlacement placement = pose.ResolvePlacement(snapshot, deltaT);
        string mode = "Freeflight";
        List<ObjectId> references = [];
        Vector3d localPosition;
        Func<Vector3d, Vector3d> coordinates = ToEcliptic;
        if (pose.Tracking && pose.Target is { } target && pose.Placement?.Frame != NavigationFrame.Equatorial)
        {
            if (!world.TryNavigationState(target, snapshot, out var body, deltaT))
                throw new InvalidDataException("The followed body has no state for a legacy link.");
            references.Add(target);
            localPosition = pose.AnchorKilometers + placement.OffsetKilometers - body.PositionKilometers;
            mode = "Follow";
            if (pose.Placement is { UsesFrame: true } frame)
            {
                if (!pose.TryFrameBasis(snapshot, deltaT, out var basis))
                    throw new InvalidDataException("The observer frame cannot be exported at this epoch.");
                Basis3d inverse = basis.Transpose();
                if (frame.Frame == NavigationFrame.Surface && frame.Reference is { } surfaceBody &&
                    snapshot.TryState(surfaceBody, out var surfaceOrigin))
                {
                    references[0] = surfaceBody;
                    localPosition = pose.AnchorKilometers + placement.OffsetKilometers - surfaceOrigin.PositionKilometers;
                }
                if (frame.BodyFixed || frame.Frame == NavigationFrame.Surface)
                {
                    mode = "SyncOrbit";
                    coordinates = vector =>
                    {
                        Vector3d local = inverse.Transform(vector);
                        return new(local.X, local.Z, -local.Y);
                    };
                }
                else
                {
                    mode = frame.Frame == NavigationFrame.Chase ? "Chase" : "PhaseLock";
                    coordinates = inverse.Transform;
                    if (frame.Frame == NavigationFrame.PhaseLock)
                        references.Add(frame.Reference!.Value);
                    else if (world.Catalogue.Find(target)?.Parent != frame.Reference)
                        throw new InvalidDataException("The legacy chase frame uses the body's catalogue parent.");
                }
            }
        }
        else
        {
            if (!snapshot.TryState(SolarSystemSample.SunId, out var sun))
                throw new InvalidDataException("The Sun has no state for a legacy link.");
            localPosition = pose.AnchorKilometers + placement.OffsetKilometers - sun.PositionKilometers;
        }

        Vector3d offset = coordinates(localPosition);
        var attitude = new NavigationPlacement(default, coordinates(placement.Forward), coordinates(placement.Up));
        Quaternion orientation = Quaternion.Conjugate(attitude.ToQuaternion());
        var link = new StringBuilder(LegacyScheme).Append(mode);
        foreach (ObjectId id in references)
            link.Append('/').Append(CataloguePath(world.Catalogue, id));
        var sinceJ2000 = observer.Epoch.DifferenceNanoseconds(new(JulianInstant.J2000Day, 0, TimeScale.Tdb));
        var date = new DateTime(2000, 1, 1, 12, 0, 0).AddTicks(checked((long)(sinceJ2000 / 100)));
        link.Append('/').Append(date.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        link.Append("?x=").Append(WriteCoordinate(offset.X)).Append("&y=").Append(WriteCoordinate(offset.Y))
            .Append("&z=").Append(WriteCoordinate(offset.Z));
        link.Append("&ow=").Append(Number(orientation.W)).Append("&ox=").Append(Number(orientation.X))
            .Append("&oy=").Append(Number(orientation.Y)).Append("&oz=").Append(Number(orientation.Z));
        if (observer.Selection is { } selection)
            link.Append("&select=").Append(CataloguePath(world.Catalogue, selection));
        link.Append("&fov=").Append(Number(observer.EffectiveFieldOfView))
            .Append("&ts=").Append(Number(observer.PlaybackRate ?? SimulationSession.DefaultPlaybackRate(observer.Mode)))
            .Append("&ltd=0&p=").Append(observer.Playing ? '0' : '1').Append("&tsrc=0&ver=3");
        return link.ToString();

        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string CataloguePath(ObjectCatalogue catalogue, ObjectId id)
    {
        Stack<string> names = new();
        for (ObjectId? current = id; current is { } value;)
        {
            if (value == SolarSystemSample.SunId)
            {
                names.Push("Sol");
                break;
            }
            CatalogueObject item = catalogue.Find(value) ?? throw new InvalidDataException("The scene references an unknown object.");
            names.Push(Uri.EscapeDataString(item.Name));
            current = item.Parent;
        }
        return string.Join(':', names);
    }

    private static Vector3d ToEcliptic(Vector3d vector)
    {
        var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
        return new(vector.X, vector.Z * cos - vector.Y * sin, -vector.Y * cos - vector.Z * sin);
    }

    private static string WriteCoordinate(double kilometers)
    {
        const double fixedScale = 18_446_744_073_709_551_616d;
        Int128 raw = checked((Int128)Math.Round(kilometers * 1e6 / StellarEntry.KilometersPerLightYear * fixedScale));
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(bytes, raw);
        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == 0)
            length--;
        return Convert.ToBase64String(bytes[..length]).TrimEnd('=');
    }
}
