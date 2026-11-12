// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>Reconstructs the archived single camera against the installed ephemeris.</summary>
public sealed class LegacyCameraSequence : IObserverReplay
{
    private readonly List<ObserverView> _views = [];
    private readonly DeltaTModel _deltaT;
    private int _next;
    private bool _disposed;

    public LegacyCameraSequence(Stream stream, SimulationWorld world, DeltaTModel deltaT)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        if (!stream.CanRead || !stream.CanSeek || stream.Length - stream.Position > 512L * 1024 * 1024)
            throw new InvalidDataException("Archived camera replay needs a readable, seekable file of up to 512 MiB.");
        _deltaT = deltaT;
        WorldSnapshot? snapshot = null;
        JulianInstant? lastEpoch = null;
        var anchors = new Dictionary<uint, ObjectId?>();
        FrameCount = LegacyFrameInspection.Inspect(stream, AddFrame, default).Frames;

        void AddFrame(ReadOnlySpan<byte> data, int minor, IReadOnlyList<string> strings)
        {
            int bodyStart = minor >= 9 ? 112 : 110;
            if (data.Length < bodyStart || _views.Count >= 100_000 || data[40] != 0)
                throw new InvalidDataException("Archived camera replay needs at most 100,000 TDB frames.");
            var epoch = new JulianInstant(BinaryPrimitives.ReadInt64LittleEndian(data[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(data[32..]), TimeScale.Tdb);
            if (lastEpoch != epoch)
            {
                if (epoch.CompareTo(world.Ephemeris.Start) < 0 || epoch.CompareTo(world.Ephemeris.End) > 0)
                    throw new InvalidDataException("An archived camera epoch lies outside the installed ephemeris.");
                snapshot = world.Graph.Evaluate(epoch);
                lastEpoch = epoch;
            }

            double scale = BinaryPrimitives.ReadDoubleLittleEndian(data[57..]);
            double field = Single(data[45..]);
            Vector3d axis = Vector(data[65..]) * -1;
            double w = Single(data[77..]);
            double norm = Math.Sqrt(w * w + Vector3d.Dot(axis, axis));
            if (!double.IsFinite(scale) || scale <= 0 || !ObserverView.ValidFieldOfView(field) ||
                !double.IsFinite(norm) || Math.Abs(norm - 1) > .001)
                throw new InvalidDataException("An archived camera has an invalid projection or orientation.");
            axis /= norm;
            w /= norm;

            ObjectId? target = null;
            Vector3d best = default, anchor = default;
            double distance = double.PositiveInfinity;
            int bodies = BinaryPrimitives.ReadUInt16LittleEndian(data[98..]);
            if (bodies > 192)
                throw new InvalidDataException("Archived camera replay exceeds the body limit.");
            int bodyLength = 69 + (minor >= 4 ? 24 : 0) + (minor >= 5 ? 36 : 0) + (minor >= 6 ? 8 : 0);
            int cursor = bodyStart;
            for (int index = 0; index < bodies; index++)
            {
                if (data.Length - cursor < bodyLength + (minor >= 8 ? 1 : 0))
                    throw new InvalidDataException("An archived camera body is truncated.");
                var body = data[cursor..];
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (id >= strings.Count)
                    throw new InvalidDataException("An archived camera refers to an undefined body name.");
                if (!anchors.TryGetValue(id, out ObjectId? key))
                {
                    try { key = SceneLink.ResolveObject(strings[(int)id], world.Catalogue); }
                    catch (InvalidDataException) { key = null; }
                    anchors.Add(id, key);
                }
                if (key is { } known)
                    Consider(known, Vector(body[4..]));
                cursor += bodyLength;
                if (minor >= 8)
                {
                    byte native = data[cursor++];
                    if (native > 1 || native == 1 && data.Length - cursor < 36)
                        throw new InvalidDataException("An archived camera body has an invalid native transform.");
                    if (native == 1)
                        cursor += 36;
                }
            }
            if (target is null && Single(data[93..]) > 0)
                Consider(SolarSystemSample.SunId, Vector(data[81..]));
            if (target is null)
                throw new InvalidDataException("An archived camera has no usable ephemeris anchor.");

            var placement = new NavigationPlacement(ToWorld(best * -1), ToWorld(new(0, 0, -1)), ToWorld(new(0, 1, 0)));
            var pose = new NavigationCamera(anchor, Math.Max(NavigationCamera.MinRange, distance), 0, 0, target, false)
                { Placement = placement };
            if (!pose.IsValid())
                throw new InvalidDataException("An archived camera lies outside the supported navigation range.");
            byte flags = data[97];
            var sky = new SkyLayerSettings(true, false, (flags & 2) != 0, (flags & 1) != 0)
                { DeepSky = (flags & 64) != 0, ConstellationArt = (flags & 128) != 0 };
            _views.Add(new(epoch, CameraMode.Solar, target, false)
                { Navigation = pose, FieldOfViewDegrees = field, Sky = sky });

            void Consider(ObjectId id, Vector3d recorded)
            {
                Vector3d offset = recorded * scale;
                double range = offset.Length;
                if (range < distance && range <= NavigationCamera.MaxRange && snapshot!.TryState(id, out CartesianState state))
                {
                    target = id;
                    best = offset;
                    anchor = state.PositionKilometers;
                    distance = range;
                }
            }

            Vector3d ToWorld(Vector3d vector)
            {
                Vector3d twiceCross = Vector3d.Cross(axis, vector) * 2;
                return SceneLink.FromEcliptic(vector + twiceCross * w + Vector3d.Cross(axis, twiceCross));
            }
        }
    }

    public int FramesPerSecond => 30;
    public int FrameCount { get; }

    public SimulationScenario? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _next == _views.Count ? null : new([_views[_next++]], true, 0, 0, _deltaT);
    }

    public void Dispose()
    {
        _disposed = true;
        _views.Clear();
    }

    private static float Single(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadSingleLittleEndian(value);
    private static Vector3d Vector(ReadOnlySpan<byte> value) => new(Single(value), Single(value[4..]), Single(value[8..]));
}
