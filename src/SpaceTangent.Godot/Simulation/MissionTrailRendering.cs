// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class MissionTrailRendering
{
    private readonly Node3D _parent;
    internal MissionTrailRendering(Node3D parent) => _parent = parent;
    internal void Hide() => _missionTrailNode?.Hide();
    private bool _closed;
    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        Reset();
        _missionTrailNode?.MaterialOverride?.Dispose();
        _missionTrailMesh?.Dispose();
        if (_missionTrailNode is { } node) { node.Mesh = null; node.MaterialOverride = null; }
        _missionTrailMesh = null;
        _missionTrailNode = null;
    }
    private MeshInstance3D? _missionTrailNode;
    private ImmediateMesh? _missionTrailMesh;
    private readonly RollingSamples<(SimulationWorld World, ObjectId Id, ObjectId Center, MissionDisplay Style, DeltaTModel? Model), JulianInstant,
        (Vector3d? Offset, float Shade)> _missionTrail = new();
    private (Vector3d? Offset, float Shade)[] _missionTrailSamples = [];
    private static readonly FrameSamplesCache<(ObjectId, ObjectId, JulianInstant, MissionDisplay, DeltaTModel?), (Vector3d? Offset, float Shade)> TrailBatches = new();
    internal bool HasPending => _missionTrail.HasPending;
    internal JulianInstant? MissionTrailEpoch => _missionTrail.PublishedEpoch;
    internal IReadOnlyList<(Vector3d? Offset, float Shade)> MissionTrailSamples => Array.AsReadOnly(_missionTrailSamples);
    internal void Reset()
    {
        _missionTrail.Reset();
        _missionTrailSamples = [];
        _missionTrailNode?.Hide();
        MissionTrailEdgeColors = null;
    }

    internal (Color First, Color Last)? MissionTrailEdgeColors { get; private set; }
    internal bool MissionTrailVisible => _missionTrailNode?.Visible == true;
    internal int MissionTrailPointCount => _missionTrailSamples.Count(point => point.Offset.HasValue);
    internal Vector3d? MissionTrailFirstPoint => _missionTrailSamples.FirstOrDefault().Offset;
    internal Vector3d? MissionTrailLastPoint => _missionTrailSamples.LastOrDefault().Offset;

    internal void Draw(SimulationWorld? world, WorldSnapshot snapshot, ObjectId? target,
        Func<Vector3d, (Vector3 Position, double Scale)> project, bool showOrbits, bool incremental, bool archived)
    {
        if (_closed) return;
        _missionTrailNode?.Hide();
        if (!showOrbits || world is null || target is not { } id ||
            !snapshot.TryStateCenter(id, out var center) || !snapshot.TryState(center, out var origin))
        {
            Reset();
            return;
        }
        var satellite = world.Satellites.FirstOrDefault(item => item.Id == id);
        var mission = world.FindSampledMission(id);
        if ((satellite?.Display ?? mission?.Display) is not { } style ||
            (style.OrbitDurationSeconds ?? satellite?.OrbitPeriodSeconds) is not > 0)
        {
            Reset();
            return;
        }
        double duration = style.OrbitDurationSeconds ?? satellite!.OrbitPeriodSeconds;
        double lead = style.OrbitLeadSeconds;
        double span = duration + lead;
        if (!double.IsFinite(span) || span > JulianInstant.MaximumDay * (double)JulianInstant.SecondsPerDay)
        { Reset(); return; }
        var epoch = snapshot.Epoch;
        _missionTrail.Continuous((world, id, center, style, snapshot.DeltaT), epoch, (long)Engine.GetProcessFrames(), span / 256,
            static (a, b) => Math.Abs((double)a.DifferenceNanoseconds(b)) / JulianInstant.NanosecondsPerSecond,
            CreateBatch, !incremental || archived);
        if (_missionTrail.LastSamples > 0) RuntimeProfile.Record("trail.slice", _missionTrail.LastMilliseconds);
        _missionTrailSamples = _missionTrail.Published ?? [];

        FrameSamples<(Vector3d? Offset, float Shade)> CreateBatch(JulianInstant atEpoch)
        {
            using var profile = RuntimeProfile.Measure("trail.request");
            const int count = 65;
            var minimum = new JulianInstant(0, 0, TimeScale.Tdb);
            var maximum = new JulianInstant(JulianInstant.MaximumDay, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb);
            double first = Math.Max(-duration, (double)minimum.DifferenceNanoseconds(atEpoch) / JulianInstant.NanosecondsPerSecond);
            double last = Math.Min(lead, (double)maximum.DifferenceNanoseconds(atEpoch) / JulianInstant.NanosecondsPerSecond);
            var deltaT = snapshot.DeltaT;
            return TrailBatches.Get(world, (id, center, atEpoch, style, snapshot.DeltaT), () => new(count, index =>
            {
                double fraction = index / (double)(count - 1);
                var at = atEpoch.AddSeconds(first + (last - first) * fraction);
                var point = deltaT is { } model ? world.Graph.Evaluate(at, model) : world.Graph.Evaluate(at);
                Vector3d? offset = point.TryStateCenter(id, out var sampleCenter) && sampleCenter == center &&
                    point.TryState(id, out var state) && point.TryState(center, out var parent)
                    ? (state - parent).PositionKilometers : null;
                return (offset, (float)(1 - style.OrbitFade * (1 - fraction)));
            }), incremental && !archived);
        }

        if (!_missionTrailSamples.Zip(_missionTrailSamples.Skip(1)).Any(pair => pair.First.Offset.HasValue && pair.Second.Offset.HasValue))
            return;
        if (_missionTrailNode is null)
        {
            _missionTrailMesh = new ImmediateMesh();
            _missionTrailNode = new MeshInstance3D
            {
                Mesh = _missionTrailMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    VertexColorUseAsAlbedo = true,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
                }
            };
            _parent.AddChild(_missionTrailNode);
        }
        var mesh = _missionTrailMesh!;
        var color = style.OrbitColor ?? new Vector3d(.55, .7, .95);
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int index = 1; index < _missionTrailSamples.Length; index++)
        {
            var previous = _missionTrailSamples[index - 1];
            var current = _missionTrailSamples[index];
            if (previous.Offset is { } from && current.Offset is { } to)
            {
                Vertex(from, previous.Shade);
                Vertex(to, current.Shade);
            }
        }
        mesh.SurfaceEnd();
        _missionTrailNode.Show();
        MissionTrailEdgeColors = (Tint(_missionTrailSamples[0].Shade), Tint(_missionTrailSamples[^1].Shade));

        Color Tint(float shade) => new((float)color.X * shade, (float)color.Y * shade, (float)color.Z * shade);

        void Vertex(Vector3d offset, float shade)
        {
            mesh.SurfaceSetColor(Tint(shade));
            mesh.SurfaceAddVertex(project(origin.PositionKilometers + offset).Position);
        }
    }
}
