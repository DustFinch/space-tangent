// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MissionTrailRendering? _missionTrailRendering;
    private MissionTrailRendering Trail => _missionTrailRendering ??= new(this);
    internal bool IncrementalPaths { get; set; } = true;
    internal bool HasPendingPaths => _missionTrailRendering?.HasPending == true || _surface?.HasPendingPath == true || _bodyObserver is not null && _bodySkyPath.HasPending;
    internal JulianInstant? MissionTrailEpoch => _missionTrailRendering?.MissionTrailEpoch;
    internal double? SelectedBodyPathDay => _bodyObserver is null ? _surface?.BodyPathDay : _bodySkyPath.PublishedDay;
    internal IReadOnlyList<(Vector3d? Offset, float Shade)> MissionTrailSamples => Trail.MissionTrailSamples;
    internal (Color First, Color Last)? MissionTrailEdgeColors => _missionTrailRendering?.MissionTrailEdgeColors;
    internal bool MissionTrailVisible => _missionTrailRendering?.MissionTrailVisible == true;
    internal int MissionTrailPointCount => _missionTrailRendering?.MissionTrailPointCount ?? 0;
    internal Vector3d? MissionTrailFirstPoint => _missionTrailRendering?.MissionTrailFirstPoint;
    internal Vector3d? MissionTrailLastPoint => _missionTrailRendering?.MissionTrailLastPoint;
    private void CancelMissionTrail() => _missionTrailRendering?.Reset();
    private void DrawMissionTrail(WorldSnapshot snapshot, ObjectId? target, Func<Vector3d, (Vector3 Position, double Scale)> project) =>
        Trail.Draw(CatalogueWorld, snapshot, target, project, ShowPlanetOrbits, IncrementalPaths, ArchivedGeometry is not null);
}
