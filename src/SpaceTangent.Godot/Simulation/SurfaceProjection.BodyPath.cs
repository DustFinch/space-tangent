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

    private (double FirstDay, Vector3?[] Directions)? BodyPath(SimulationWorld world, ObjectId target,
        JulianInstant epoch, DeltaTModel model)
    {
        double today = Math.Floor(epoch.ToJulianDate() - .5) + .5;
        var key = (world, target, today, model);
        if (_bodyPathKey == key)
            return _bodyPath;
        _bodyPathKey = key;
        _bodyPath = null;
        var kind = world.Catalogue.Find(target)?.Kind;
        if (target == SolarSystemSample.EarthId ||
            kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.DwarfPlanet or CatalogueKind.Spacecraft))
            return null;

        // The archived path uses the centre of the observing body as its origin.
        double first = today - 60;
        var directions = new Vector3?[121];
        for (int day = 0; day < directions.Length; day++)
        {
            var instant = JulianInstant.FromJulianDate(first + day, TimeScale.Tdb);
            var snapshot = world.Graph.Evaluate(instant);
            if (snapshot.SolarSystem is not { } solar || !world.TryNavigationState(target, snapshot, out var state, model))
                continue;
            Vector3d offset = (state - solar.Earth).PositionKilometers;
            if (offset.Length > 0 && offset.IsFinite)
                directions[day] = Convert(offset / offset.Length);
        }
        _bodyPath = (first, directions);
        return _bodyPath;
    }
}
