// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private RecordedStarRendering? _recordedStars;
    private RecordedStarRendering RecordedStars => _recordedStars ??= new(this);
    internal int ArchivedStarCount => _recordedStars?.ArchivedStarCount ?? 0;
    internal MeshInstance3D? ArchivedGroundGlow => _recordedStars?.ArchivedGroundGlow;
    private void DrawArchivedStars(RecordedStellarView? view, double julianDate) =>
        RecordedStars.DrawStars(view, julianDate, _navigationCamera);
    private void DrawArchivedStarPoints(IReadOnlyList<RecordedStarPoint>? points) =>
        RecordedStars.DrawPoints(points, _navigationCamera);
    private void DrawArchivedGroundGlow(RecordedSceneGeometry? frame) =>
        RecordedStars.DrawGroundGlow(frame, _navigationCamera);
}
