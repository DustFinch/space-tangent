// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MeshInstance3D? _missionTrailNode;
    private ImmediateMesh? _missionTrailMesh;
    private (SimulationWorld World, ObjectId Id, ObjectId Center, JulianInstant Epoch, MissionDisplay Style)? _missionTrailKey;
    private (Vector3d? Offset, float Shade)[] _missionTrailSamples = [];

    internal (Color First, Color Last)? MissionTrailEdgeColors { get; private set; }
    internal bool MissionTrailVisible => _missionTrailNode?.Visible == true;
    internal int MissionTrailPointCount => _missionTrailSamples.Count(point => point.Offset.HasValue);
    internal Vector3d? MissionTrailFirstPoint => _missionTrailSamples.FirstOrDefault().Offset;
    internal Vector3d? MissionTrailLastPoint => _missionTrailSamples.LastOrDefault().Offset;

    private void DrawMissionTrail(WorldSnapshot snapshot, ObjectId? target, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _missionTrailNode?.Hide();
        if (!ShowPlanetOrbits || CatalogueWorld is not { } world || target is not { } id ||
            !snapshot.TryStateCenter(id, out var center) || !snapshot.TryState(center, out var origin))
            return;
        var satellite = world.Satellites.FirstOrDefault(item => item.Id == id);
        var mission = world.FindSampledMission(id);
        if ((satellite?.Display ?? mission?.Display) is not { } style ||
            (style.OrbitDurationSeconds ?? satellite?.OrbitPeriodSeconds) is not > 0)
            return;
        double duration = style.OrbitDurationSeconds ?? satellite!.OrbitPeriodSeconds;
        double lead = style.OrbitLeadSeconds;
        double span = duration + lead;
        if (!double.IsFinite(span) || span > JulianInstant.MaximumDay * (double)JulianInstant.SecondsPerDay)
            return;
        var epoch = snapshot.Epoch;
        if (_missionTrailKey is not { } key || !ReferenceEquals(key.World, world) || key.Id != id ||
            key.Center != center || key.Style != style ||
            Math.Abs((double)epoch.DifferenceNanoseconds(key.Epoch)) > span * JulianInstant.NanosecondsPerSecond / 256)
        {
            _missionTrailKey = (world, id, center, epoch, style);
            const int count = 65;
            _missionTrailSamples = new (Vector3d?, float)[count];
            var minimum = new JulianInstant(0, 0, TimeScale.Tdb);
            var maximum = new JulianInstant(JulianInstant.MaximumDay, JulianInstant.NanosecondsPerDay - 1, TimeScale.Tdb);
            double first = Math.Max(-duration, (double)minimum.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond);
            double last = Math.Min(lead, (double)maximum.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond);
            for (int index = 0; index < count; index++)
            {
                double fraction = index / (double)(count - 1);
                var at = epoch.AddSeconds(first + (last - first) * fraction);
                var point = world.Graph.Evaluate(at);
                Vector3d? offset = point.TryStateCenter(id, out var sampleCenter) && sampleCenter == center &&
                    point.TryState(id, out var state) && point.TryState(center, out var parent)
                    ? (state - parent).PositionKilometers : null;
                _missionTrailSamples[index] = (offset, (float)(1 - style.OrbitFade * (1 - fraction)));
            }
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
            AddChild(_missionTrailNode);
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

