// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public readonly record struct RecordedBodyGeometry(string Name, ObjectId? Id, Vector3d CameraPosition, double Radius,
    double PolarScale, Basis3d CameraBasis, Vector3d LightDirection,
    Vector3d LightColor, Vector3d SecondLightDirection, Vector3d SecondLightColor, double AtmosphereRadius)
{
    public string? MaterialId { get; init; }
    public double RingInnerRadius { get; init; }
    public double RingOuterRadius { get; init; }
    public double LightAngularRadius { get; init; }
    public double SecondLightAngularRadius { get; init; }
}

public sealed record RecordedSceneGeometry(double KilometersPerUnit, IReadOnlyList<RecordedBodyGeometry> Bodies)
{
    public IReadOnlyList<RecordedOrbit> Orbits { get; init; } = [];
    public IReadOnlyList<RecordedLabel> Labels { get; init; } = [];
    public IReadOnlyList<RecordedStarPoint> StarPoints { get; init; } = [];
    public RecordedLens? Lens { get; init; }
    public RecordedStellarView? StellarView { get; init; }
    public IReadOnlyList<RecordedCometTail> CometTails { get; init; } = [];
    public IReadOnlyList<RecordedShadow> Shadows { get; init; } = [];
    public Vector3d SunPosition { get; init; }
    public double SunRadius { get; init; }
    public IReadOnlyDictionary<string, RecordedBodyPoint> BodyPoints { get; init; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, RecordedBodyPoint>(new Dictionary<string, RecordedBodyPoint>());
}

/// <summary>Reconstructs the archived single camera against the installed ephemeris.</summary>
public sealed class LegacyCameraSequence : IObserverReplay
{
    private readonly List<ObserverView> _views = [];
    private readonly List<RecordedSceneGeometry> _geometry = [];
    private const int MaximumRetainedBodies = 1_000_000;
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
        RecordedSceneGeometry? previousGeometry = null;
        RecordedBodyGeometry[]? previousBodies = null;
        int retainedBodies = 0;
        var overlays = new LegacyFrameOverlays();
        var stellar = new LegacyFrameStars();
        FrameCount = LegacyFrameInspection.Inspect(stream, AddFrame, default, overlays.AddOrbitGeometry, stellar.ReadField).Frames;

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
            var geometry = new RecordedBodyGeometry[bodies];
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
                Vector3d position = Vector(body[4..]);
                if (key is { } known)
                    Consider(known, position);
                uint material = BinaryPrimitives.ReadUInt32LittleEndian(body[48..]);
                if (material >= strings.Count)
                    throw new InvalidDataException("An archived camera refers to an undefined body material.");
                double radius = Single(body[16..]), polarScale = Single(body[65..]);
                if (!position.IsFinite || !double.IsFinite(radius) || radius <= 0 ||
                    !double.IsFinite(polarScale) || polarScale <= 0)
                    throw new InvalidDataException("Invalid archived body position or radius.");
                Vector3d imaginary = Vector(body[20..]);
                double real = Single(body[32..]);
                if (!imaginary.IsFinite || !double.IsFinite(real))
                    throw new InvalidDataException("Invalid archived body orientation.");
                Vector3d Column(Vector3d v)
                {
                    Vector3d twice = Vector3d.Cross(imaginary, v) * 2;
                    return v + twice * real + Vector3d.Cross(imaginary, twice);
                }
                var basis = new Basis3d(Column(new(1, 0, 0)), Column(new(0, 1, 0)), Column(new(0, 0, 1)));
                cursor += bodyLength;
                if (minor >= 8)
                {
                    byte native = data[cursor++];
                    if (native > 1 || native == 1 && data.Length - cursor < 36)
                        throw new InvalidDataException("An archived camera body has an invalid native transform.");
                    if (native == 1)
                    {
                        var transform = data[cursor..];
                        basis = new(Vector(transform), Vector(transform[12..]), Vector(transform[24..]));
                        if (!basis.X.IsFinite || !basis.Y.IsFinite || !basis.Z.IsFinite)
                            throw new InvalidDataException("Invalid archived native body orientation.");
                        cursor += 36;
                    }
                }
                Vector3d light = Vector(body[36..]);
                if (!light.IsFinite || !basis.X.IsFinite || !basis.Y.IsFinite || !basis.Z.IsFinite)
                    throw new InvalidDataException("Invalid archived body lighting or basis.");
                Vector3d lightColor = minor >= 5 ? ReadLightColor(body[93..]) : new(1, 1, 1);
                Vector3d secondDirection = minor >= 5 ? Vector(body[105..]) : default;
                Vector3d secondColor = minor >= 5 ? ReadLightColor(body[117..]) : default;
                if (!secondDirection.IsFinite)
                    throw new InvalidDataException("Invalid archived secondary light direction.");
                double atmosphereRadius = Single(body[60..]);
                if (!double.IsFinite(atmosphereRadius) || atmosphereRadius != 0 && atmosphereRadius < radius)
                    throw new InvalidDataException("Invalid archived atmosphere radius.");
                float inner = Single(body[52..]), outer = Single(body[56..]);
                float lightRadius = minor >= 6 ? Single(body[129..]) : 0;
                float secondLightRadius = minor >= 6 ? Single(body[133..]) : 0;
                if (!float.IsFinite(inner) || !float.IsFinite(outer) || inner < 0 || outer < inner ||
                    outer > 0 && inner < radius || !float.IsFinite((float)(outer / radius)) ||
                    !float.IsFinite(lightRadius) || lightRadius < 0 || !float.IsFinite(secondLightRadius) || secondLightRadius < 0)
                    throw new InvalidDataException("Invalid archived ring radii or light angular radius.");
                geometry[index] = new(strings[(int)id], key, position, radius, polarScale, basis,
                    light, lightColor, secondDirection, secondColor, atmosphereRadius)
                {
                    MaterialId = strings[(int)material], RingInnerRadius = inner, RingOuterRadius = outer, LightAngularRadius = lightRadius,
                    SecondLightAngularRadius = secondLightRadius
                };
            }
            var (starPoints, orbits, labels, points, tails, shadows, lens, stellarOffset) = overlays.Read(data, cursor, minor, strings);
            if (lens is { Name.Length: > 3 and <= 35 } compact && compact.Name.StartsWith("bh/", StringComparison.Ordinal) &&
                world.BlackHoles.Find(new("black-hole/" + compact.Name[3..])) is { } hole)
                Consider(hole.Id, compact.CameraPosition);
            var sunPosition = Vector(data[81..]);
            double sunRadius = Single(data[93..]);
            if (!sunPosition.IsFinite || !double.IsFinite(sunRadius) || sunRadius < 0)
                throw new InvalidDataException("Invalid archived solar geometry.");
            var stellarView = minor >= 10 ? stellar.ReadView(data, stellarOffset) : null;
            if (previousGeometry is null || previousGeometry.KilometersPerUnit != scale ||
                !geometry.AsSpan().SequenceEqual(previousBodies) ||
                !ReferenceEquals(orbits, previousGeometry.Orbits) || !ReferenceEquals(labels, previousGeometry.Labels) ||
                previousGeometry.StellarView != stellarView || previousGeometry.Lens != lens ||
                !ReferenceEquals(starPoints, previousGeometry.StarPoints) || !ReferenceEquals(points, previousGeometry.BodyPoints) || !ReferenceEquals(tails, previousGeometry.CometTails) ||
                !ReferenceEquals(shadows, previousGeometry.Shadows) || previousGeometry.SunPosition != sunPosition ||
                previousGeometry.SunRadius != sunRadius)
            {
                if (!geometry.AsSpan().SequenceEqual(previousBodies))
                    retainedBodies += geometry.Length;
                if (retainedBodies > MaximumRetainedBodies)
                    throw new InvalidDataException("Archived replay exceeds the body geometry budget.");
                var savedBodies = previousGeometry is not null && geometry.AsSpan().SequenceEqual(previousBodies)
                    ? previousGeometry.Bodies : Array.AsReadOnly(geometry);
                previousGeometry = new(scale, savedBodies)
                {
                    Orbits = orbits, Labels = labels, StarPoints = starPoints, Lens = lens, StellarView = stellarView, BodyPoints = points, CometTails = tails,
                    Shadows = shadows, SunPosition = sunPosition, SunRadius = sunRadius
                };
                previousBodies = geometry;
            }
            _geometry.Add(previousGeometry);
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
    public RecordedSceneGeometry? CurrentGeometry => _next == 0 ? null : _geometry[_next - 1];

    public SimulationScenario? ReadNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _next == _views.Count ? null : new([_views[_next++]], true, 0, 0, _deltaT);
    }

    public void Dispose()
    {
        _disposed = true;
        _views.Clear();
        _geometry.Clear();
        _next = 0;
    }

    private static Vector3d ReadLightColor(ReadOnlySpan<byte> value)
    {
        var color = Vector(value);
        if (color.X is not (>= 0 and <= 1000) || color.Y is not (>= 0 and <= 1000) ||
            color.Z is not (>= 0 and <= 1000))
            throw new InvalidDataException("Invalid archived light color.");
        return color;
    }

    private static float Single(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadSingleLittleEndian(value);
    private static Vector3d Vector(ReadOnlySpan<byte> value) => new(Single(value), Single(value[4..]), Single(value[8..]));
}
