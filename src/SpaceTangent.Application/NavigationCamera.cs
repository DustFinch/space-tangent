// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>A view's inertial pivot, orbital camera pose, and optional tracked object.</summary>
public readonly record struct NavigationCamera(Vector3d AnchorKilometers, double RangeKilometers,
    double AzimuthRadians, double ElevationRadians, ObjectId? Target, bool Tracking)
{
    public NavigationPlacement? Placement { get; init; }

    internal const double MaxElevation = 1.55;
    internal const double MinRange = .005;
    internal const double MaxRange = 1e23;

    public bool IsValid() => AnchorKilometers.IsFinite && AnchorKilometers.Length <= 1e23 &&
        RangeKilometers is >= MinRange and <= MaxRange &&
        AzimuthRadians is >= -Math.PI and <= Math.PI &&
        ElevationRadians is >= -MaxElevation and <= MaxElevation &&
        (!Tracking || Target.HasValue) &&
        (Placement is not { } placement || (placement.IsValid(RangeKilometers) &&
            (!placement.UsesFrame || Tracking) && (placement.Reference is null || placement.Reference != Target)));

    public NavigationPlacement ResolvePlacement(WorldSnapshot snapshot, DeltaTModel deltaT = DeltaTModel.MorrisonStephenson2004)
    {
        if (Placement is { } local)
        {
            if (!local.UsesFrame)
                return local;
            if (TryFrameBasis(snapshot, deltaT, out Basis3d basis))
                return local.Transform(basis, false);
        }
        var (sinA, cosA) = Math.SinCos(AzimuthRadians);
        var (sinE, cosE) = Math.SinCos(ElevationRadians);
        Vector3d outward = new(cosE * sinA, -cosE * cosA, sinE);
        Vector3d forward = outward * -1;
        Vector3d right = Vector3d.Cross(forward, new(0, 0, 1));
        Vector3d up = Vector3d.Cross(right / right.Length, forward);
        return new(outward * RangeKilometers, forward, up);
    }

    internal bool TryFrameBasis(WorldSnapshot snapshot, DeltaTModel deltaT, out Basis3d basis)
    {
        basis = default;
        if (Target is not { } target || Placement is not { } placement)
            return false;
        if (placement is { Frame: NavigationFrame.Surface, Reference: { } body })
            return TryBodyBasis(snapshot, body, deltaT, out basis);
        if (placement.Frame == NavigationFrame.Equatorial)
            return TryMeanEquator(snapshot, target, out basis);
        if (placement.CatalogueFrame is { } catalogue)
            return TryCatalogueReference(snapshot, target, catalogue, out _, out basis);
        return placement.BodyFixed
            ? TryBodyBasis(snapshot, target, deltaT, out basis)
            : placement.Reference is { } reference && TryRelativeBasis(snapshot, placement.Frame, target, reference, out basis);
    }

    internal static bool TryCatalogueReference(WorldSnapshot snapshot, ObjectId target, CatalogueCameraFrame frame,
        out Vector3d origin, out Basis3d basis)
    {
        origin = default;
        basis = default;
        if (!snapshot.TryMissionReferenceFrame(target, frame.Orbit, frame.SelectionEpoch, out var state, out var reference))
            return false;
        Vector3d z = reference.ToWorld.Z;
        if (!(z.Length > 1e-12) || !z.IsFinite)
            return false;
        z /= z.Length;
        Vector3d x = reference.ToWorld.X - z * Vector3d.Dot(reference.ToWorld.X, z);
        if (!(x.Length > 1e-12) || !x.IsFinite)
            return false;
        x /= x.Length;
        origin = state.PositionKilometers;
        basis = new(x, Vector3d.Cross(z, x), z);
        return true;
    }

    private static bool TryMeanEquator(WorldSnapshot snapshot, ObjectId target, out Basis3d basis)
    {
        if (target == SolarSystemSample.EarthId)
        {
            basis = Basis3d.Identity;
            return snapshot.SolarSystem is not null;
        }
        basis = default;
        if (!snapshot.TryOrientation(target, out var orientation))
            return false;
        Vector3d pole = orientation.ToWorld.Z;
        if (!(pole.Length > 0))
            return false;
        pole /= pole.Length;
        Vector3d node = Vector3d.Cross(new(0, 0, 1), pole);
        if (node.Length < 1e-12)
            node = new(1, 0, 0);
        else
            node /= node.Length;
        basis = new(node, Vector3d.Cross(pole, node), pole);
        return true;
    }

    internal static bool TryBodyBasis(WorldSnapshot snapshot, ObjectId target, DeltaTModel deltaT, out Basis3d basis)
    {
        if (target == SolarSystemSample.EarthId && snapshot.SolarSystem is { } solar)
        {
            try
            {
                Basis3d local = EarthSurfaceLighting.Sample(solar, default, deltaT).EastNorthUp;
                basis = new(local.Z, local.X, local.Y);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                basis = default;
                return false;
            }
        }
        bool available = snapshot.TryOrientation(target, out FrameKinematics rotation);
        basis = rotation.ToWorld;
        return available;
    }

    internal static bool TryRelativeBasis(WorldSnapshot snapshot, NavigationFrame frame, ObjectId target, ObjectId reference, out Basis3d basis)
    {
        basis = default;
        if (frame is not (NavigationFrame.Chase or NavigationFrame.PhaseLock) ||
            !snapshot.TryState(target, out CartesianState body) || !snapshot.TryState(reference, out CartesianState other))
            return false;
        CartesianState relative = other - body;
        (Vector3d primary, Vector3d guide) = frame == NavigationFrame.Chase
            ? (relative.VelocityKilometersPerSecond, relative.PositionKilometers)
            : (relative.PositionKilometers, relative.VelocityKilometersPerSecond);
        double primaryLength = primary.Length;
        if (!(primaryLength > 0) || !double.IsFinite(primaryLength))
            return false;
        Vector3d x = primary / primaryLength;
        Vector3d y = guide - x * Vector3d.Dot(x, guide);
        if (y.Length < 1e-9 * Math.Max(guide.Length, 1e-300))
        {
            var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
            Vector3d north = new(0, -sin, cos);
            Vector3d fallback = Math.Abs(Vector3d.Dot(x, north)) < .9 ? north : new(1, 0, 0);
            y = fallback - x * Vector3d.Dot(x, fallback);
        }
        y /= y.Length;
        basis = new(x, y, Vector3d.Cross(x, y));
        return true;
    }

    internal NavigationCamera Adjust(double azimuth, double elevation, double zoom, double minimumRange) =>
        this with
        {
            AzimuthRadians = Math.IEEERemainder(AzimuthRadians + azimuth, 2 * Math.PI),
            ElevationRadians = Math.Clamp(ElevationRadians + elevation, -MaxElevation, MaxElevation),
            RangeKilometers = Math.Clamp(RangeKilometers * zoom, minimumRange, MaxRange)
        };

    public static double BodyRadiusKilometers(ObjectId target)
    {
        if (target == SolarSystemSample.SunId)
            return 695_700;
        if (PlanetCatalogue.Find(target) is { } planet)
            return planet.RadiusKilometers;
        if (target == SolarSystemSample.MoonId)
            return 1737.4;
        return NaturalBodyCatalogue.Find(target)?.RadiusKilometers ?? .007;
    }
}
