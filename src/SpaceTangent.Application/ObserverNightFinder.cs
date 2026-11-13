// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public static class ObserverNightFinder
{
    public static ObserverNight ForSelection(SimulationWorld world, ObserverView observer, DeltaTModel model,
        CancellationToken cancellation = default)
    {
        var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
        var site = camera.Site;
        var clock = camera.CivilClock.GetValueOrDefault();
        var date = clock.DateAt(observer.Epoch, site, model);
        var star = observer.Selection is { } id ? world.BrightStars.Find(id) : null;
        return ObserverEphemerides.Night(world.Ephemeris, date, site, model, (solar, light) =>
        {
            if (star is not null)
                return EarthObserving.HorizontalDirection(light, star.DirectionAt(solar.Epoch));
            if (observer.Selection is not { } target)
                return null;
            var snapshot = world.Graph.Evaluate(solar.Epoch);
            return world.TryNavigationState(target, snapshot, out CartesianState state, model)
                ? EarthObserving.Horizontal(light, (state - solar.Earth).PositionKilometers) : null;
        }, cancellation: cancellation, clock: clock);
    }
}
