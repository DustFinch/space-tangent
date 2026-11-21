// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private readonly ObserverBodyPath _bodyPath = new();
    internal bool IncrementalPaths { get; set; } = true;
    internal bool HasPendingPath => _surface.Visible && _bodyPath.HasPending;
    internal double? BodyPathDay => _bodyPath.PublishedDay;
    internal void CancelBodyPath()
    {
        _bodyPath.Reset();
    }

    private (double FirstDay, Vector3?[] Directions)? BodyPath(SimulationWorld world, ObjectId target,
        JulianInstant epoch, DeltaTModel model)
    {
        return _bodyPath.Sample(world, SolarSystemSample.EarthId, target, epoch, model, IncrementalPaths);
    }
}
