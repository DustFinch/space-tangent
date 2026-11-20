// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private (SimulationWorld World, ObjectId Target, double Day, DeltaTModel Model)? _bodyPathKey;
    private (double FirstDay, Vector3?[] Directions)? _bodyPath;
    private FrameSamples<Vector3?>? _bodyPathBatch;
    private static readonly FrameSamplesCache<(ObjectId, double, DeltaTModel), Vector3?> BodyPathBatches = new();
    internal bool IncrementalPaths { get; set; } = true;
    internal bool HasPendingPath => _surface.Visible && _bodyPathBatch is not null;
    internal void CancelBodyPath()
    {
        if (_bodyPathBatch is null) return;
        _bodyPathBatch = null;
        _bodyPathKey = null;
    }

    private (double FirstDay, Vector3?[] Directions)? BodyPath(SimulationWorld world, ObjectId target,
        JulianInstant epoch, DeltaTModel model)
    {
        double today = Math.Floor(epoch.ToJulianDate() - .5) + .5;
        var key = (world, target, today, model);
        if (_bodyPathKey == key && _bodyPathBatch is null)
            return _bodyPath;
        var kind = world.Catalogue.Find(target)?.Kind;
        if (target == SolarSystemSample.EarthId ||
            kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.DwarfPlanet or CatalogueKind.Spacecraft))
        { CancelBodyPath(); return null; }

        // The archived path uses the centre of the observing body as its origin.
        double first = today - 60;
        if (_bodyPathKey != key)
        {
            _bodyPathKey = key;
            _bodyPath = null;
            _bodyPathBatch = BodyPathBatches.Get(world, (target, today, model), () => new(121, day =>
            {
                var instant = JulianInstant.FromJulianDate(first + day, TimeScale.Tdb);
                var snapshot = world.Graph.Evaluate(instant, model);
                if (snapshot.SolarSystem is not { } solar || !world.TryNavigationState(target, snapshot, out var state, model))
                    return null;
                Vector3d offset = (state - solar.Earth).PositionKilometers;
                return offset.Length > 0 && offset.IsFinite ? Convert(offset / offset.Length) : null;
            }), IncrementalPaths);
        }
        var batch = _bodyPathBatch!;
        int previous = batch.Progress;
        bool ready = batch.Advance((long)Engine.GetProcessFrames(), !IncrementalPaths);
        if (batch.Progress != previous) RuntimeProfile.Record("ground-path.slice", batch.LastSliceMilliseconds);
        if (!ready) return null;
        _bodyPath = (first, batch.Completed!);
        _bodyPathBatch = null;
        return _bodyPath;
    }
}
