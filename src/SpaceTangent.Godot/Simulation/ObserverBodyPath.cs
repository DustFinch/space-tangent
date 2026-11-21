// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed class ObserverBodyPath
{
    private readonly RollingSamples<(SimulationWorld, ObjectId, ObjectId, DeltaTModel), double, Vector3?> _path = new();
    private JulianInstant? _lastEpoch;
    private int _direction = 1;
    private static readonly FrameSamplesCache<(ObjectId, ObjectId, double, DeltaTModel), Vector3?> Batches = new();
    internal bool HasPending => _path.HasPending;
    internal double? PublishedDay => _path.PublishedEpoch;
    internal void Reset() { _path.Reset(); _lastEpoch = null; _direction = 1; }

    internal (double FirstDay, Vector3?[] Directions)? Sample(SimulationWorld world, ObjectId origin, ObjectId target,
        JulianInstant epoch, DeltaTModel model, bool incremental)
    {
        var kind = world.Catalogue.Find(target)?.Kind;
        if (target == origin || target != SolarSystemSample.SunId &&
            kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.DwarfPlanet or CatalogueKind.Spacecraft))
        { Reset(); return null; }
        if (_lastEpoch is { } previous)
        {
            int direction = epoch.DifferenceNanoseconds(previous).CompareTo(0);
            if (direction != 0) _direction = direction;
        }
        _lastEpoch = epoch;
        double today = Math.Floor(epoch.ToJulianDate() - .5) + .5;
        double next = today + _direction;
        _path.Discrete((world, origin, target, model), today,
            next >= -.5 && next <= JulianInstant.MaximumDay + .5 ? next : null,
            (long)Engine.GetProcessFrames(), CreateBatch, !incremental);
        if (_path.LastSamples > 0) RuntimeProfile.Record("ground-path.slice", _path.LastMilliseconds);
        return _path.Published is { } directions ? (_path.PublishedEpoch!.Value - 60, directions) : null;

        FrameSamples<Vector3?> CreateBatch(double atDay) => Batches.Get(world, (origin, target, atDay, model), () => new(121, index =>
        {
            double day = atDay - 60 + index;
            if (day < 0 || day >= JulianInstant.MaximumDay + 1d) return null;
            var instant = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            if (instant.CompareTo(world.Ephemeris.Start) < 0 || instant.CompareTo(world.Ephemeris.End) > 0) return null;
            var snapshot = world.Graph.Evaluate(instant, model);
            if (!world.TryNavigationState(target, snapshot, out var state, model) || !snapshot.TryState(origin, out var body)) return null;
            var offset = (state - body).PositionKilometers;
            return offset.Length > 0 && offset.IsFinite ? new Vector3((float)(offset.X / offset.Length),
                (float)(offset.Y / offset.Length), (float)(offset.Z / offset.Length)) : null;
        }), incremental);
    }
}
