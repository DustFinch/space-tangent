// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private readonly RollingSamples<(SimulationWorld World, ObjectId Target, DeltaTModel Model), double, Vector3?> _bodyPath = new();
    private JulianInstant? _lastBodyPathEpoch;
    private int _bodyPathDirection = 1;
    private static readonly FrameSamplesCache<(ObjectId, double, DeltaTModel), Vector3?> BodyPathBatches = new();
    internal bool IncrementalPaths { get; set; } = true;
    internal bool HasPendingPath => _surface.Visible && _bodyPath.HasPending;
    internal double? BodyPathDay => _bodyPath.PublishedEpoch;
    internal void CancelBodyPath()
    {
        _bodyPath.Reset();
        _lastBodyPathEpoch = null;
        _bodyPathDirection = 1;
    }

    private (double FirstDay, Vector3?[] Directions)? BodyPath(SimulationWorld world, ObjectId target,
        JulianInstant epoch, DeltaTModel model)
    {
        double today = Math.Floor(epoch.ToJulianDate() - .5) + .5;
        var kind = world.Catalogue.Find(target)?.Kind;
        if (target == SolarSystemSample.EarthId ||
            kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.DwarfPlanet or CatalogueKind.Spacecraft))
        { CancelBodyPath(); return null; }

        // The archived path uses the centre of the observing body as its origin.
        if (_lastBodyPathEpoch is { } previousEpoch)
        {
            int direction = epoch.DifferenceNanoseconds(previousEpoch).CompareTo(0);
            if (direction != 0) _bodyPathDirection = direction;
        }
        _lastBodyPathEpoch = epoch;
        double next = today + _bodyPathDirection;
        _bodyPath.Discrete((world, target, model), today,
            next >= -.5 && next <= JulianInstant.MaximumDay + .5 ? next : null,
            (long)Engine.GetProcessFrames(), CreateBatch, !IncrementalPaths);
        if (_bodyPath.LastSamples > 0) RuntimeProfile.Record("ground-path.slice", _bodyPath.LastMilliseconds);
        return _bodyPath.Published is { } directions ? (_bodyPath.PublishedEpoch!.Value - 60, directions) : null;

        FrameSamples<Vector3?> CreateBatch(double atDay) =>
            BodyPathBatches.Get(world, (target, atDay, model), () => new(121, day =>
            {
                double sampleDay = atDay - 60 + day;
                if (sampleDay < 0 || sampleDay >= JulianInstant.MaximumDay + 1d) return null;
                var instant = JulianInstant.FromJulianDate(sampleDay, TimeScale.Tdb);
                var snapshot = world.Graph.Evaluate(instant, model);
                if (snapshot.SolarSystem is not { } solar || !world.TryNavigationState(target, snapshot, out var state, model))
                    return null;
                Vector3d offset = (state - solar.Earth).PositionKilometers;
                return offset.Length > 0 && offset.IsFinite ? Convert(offset / offset.Length) : null;
            }), IncrementalPaths);
    }
}
