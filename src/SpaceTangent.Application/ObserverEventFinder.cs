// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record ObserverEvent(JulianInstant Epoch, ObjectId Target, string Description)
{
    public HorizontalCoordinates? FlareDirection { get; init; }
}

public static class ObserverEventFinder
{
    private static ObserverEvent FlareEvent(ObjectId id, string name, SatelliteFlare flare) =>
        new(flare.Peak, id, FormattableString.Invariant(
            $"{name} flare · magnitude {flare.Magnitude:F1} · {flare.Horizontal.AltitudeDegrees:F1}° altitude"))
        {
            FlareDirection = flare.Horizontal
        };

    public static ObserverEvent? Next(SimulationWorld world, ObserverView observer, DeltaTModel deltaT,
        CancellationToken cancellation = default)
    {
        var from = observer.Epoch;
        var ephemeris = world.Ephemeris;
        var site = (observer.EarthObserver ?? EarthObserverCamera.Moscow).Site;
        var events = new List<ObserverEvent>();
        if (observer.Selection is { } selected && world.Catalogue.Find(selected) is { Kind: CatalogueKind.Spacecraft } spacecraft)
        {
            var local = world.FindLocalSatellite(selected);
            var science = world.Satellites.FirstOrDefault(track => track.Id == selected);
            if (local is null && science is null)
                return null;
            var end = SearchEnd(2);
            if (end.CompareTo(from) <= 0)
                return null;
            if (local is not null && ObservationEventSearch.HasFlarePanels(local))
            {
                var flares = ObservationEventSearch.FindSatelliteFlares(ephemeris, local, site, deltaT,
                    from, end, cancellation: cancellation);
                if (flares.Count != 0)
                {
                    var flare = flares[0];
                    return FlareEvent(selected, spacecraft.Name, flare);
                }
            }
            foreach (var pass in ObservationEventSearch.FindSatellitePasses(ephemeris, State, site, deltaT,
                         from, end, 10, cancellation))
                events.Add(new(pass.AboveHorizon.Peak, selected,
                    FormattableString.Invariant($"{spacecraft.Name} pass · {pass.PeakAltitudeDegrees:F1}° maximum altitude")));

            CartesianState? State(JulianInstant epoch)
            {
                if (local is not null && (science is null || local.Epoch.CompareTo(science.Epoch) > 0) &&
                    local.SampleInWorld(epoch, deltaT) is { } updated)
                    return updated;
                return science?.TrySample(epoch, out var sample) == true ? sample.EarthRelativeState : local?.SampleInWorld(epoch, deltaT);
            }
        }
        else
        {
            var end = SearchEnd(45);
            if (end.CompareTo(from) <= 0)
                return null;
            if (observer is { Mode: CameraMode.Surface, Selection: null })
            {
                var flareEnd = SearchEnd(3);
                foreach (var satellite in world.LocalSatellites)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!ObservationEventSearch.HasFlarePanels(satellite))
                        continue;
                    var flares = ObservationEventSearch.FindSatelliteFlares(ephemeris, satellite, site, deltaT,
                        from, flareEnd, cancellation: cancellation);
                    if (flares.Count > 0)
                        events.Add(FlareEvent(satellite.Id, satellite.Name, flares[0]));
                }
            }
            var planet = observer.Selection is { } id ? PlanetCatalogue.Find(id) : null;
            if (planet is not null && planet.Id != SolarSystemSample.EarthId)
            {
                foreach (var other in PlanetCatalogue.Planets)
                {
                    if (other.Id == planet.Id || other.Id == SolarSystemSample.EarthId)
                        continue;
                    foreach (var item in ObservationEventSearch.FindConjunctions(epoch =>
                                 Pair(planet.SystemId, other.SystemId, epoch), from, end, 2, cancellation))
                        events.Add(new(item.Peak, planet.Id, FormattableString.Invariant(
                            $"{planet.Name} and {other.Name} conjunction · {item.SeparationDegrees:F3}° separation")));
                }
            }
            else
            {
                foreach (var solar in ObservationEventSearch.FindSolarEclipses(ephemeris, site, deltaT, from, end, cancellation))
                    events.Add(new(solar.Partial.Peak, SolarSystemSample.SunId, $"{solar.Class} solar eclipse"));
                if (observer.Selection != SolarSystemSample.SunId)
                    foreach (var lunar in ObservationEventSearch.FindLunarEclipses(ephemeris, from, end, cancellation))
                        events.Add(new(lunar.Penumbral.Peak, SolarSystemSample.MoonId, $"{lunar.Class} lunar eclipse"));
            }
        }
        return events.OrderBy(item => item.Epoch).FirstOrDefault();

        JulianInstant SearchEnd(double days)
        {
            double available = (double)ephemeris.End.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
            return available >= days * JulianInstant.SecondsPerDay ? from.AddSeconds(days * JulianInstant.SecondsPerDay) : ephemeris.End;
        }

        (Vector3d, Vector3d)? Pair(ObjectId first, ObjectId second, JulianInstant epoch)
        {
            var sample = ephemeris.Sample(epoch);
            return Direction(first) is { } a && Direction(second) is { } b ? (a, b) : null;

            Vector3d? Direction(ObjectId id)
            {
                var relative = sample.RelativeState(id, SolarSystemSample.EarthId).PositionKilometers;
                var emitted = epoch.AddSeconds(-relative.Length / SimulationSession.LightKilometersPerSecond);
                if (emitted.CompareTo(ephemeris.Start) < 0)
                    return null;
                return ephemeris.Sample(emitted).RelativeState(id, SolarSystemSample.BarycenterId).PositionKilometers -
                    sample.Earth.PositionKilometers;
            }
        }
    }
}
