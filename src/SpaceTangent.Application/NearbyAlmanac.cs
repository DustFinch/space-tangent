// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record AlmanacEntry(JulianInstant Epoch, ObjectId? Target, string Description, bool DateOnly = false);

/// <summary>A bounded geocentric almanac with local eclipse visibility and global Jovian shadows.</summary>
public static class NearbyAlmanac
{
    public static IReadOnlyList<AlmanacEntry> Find(SimulationWorld world, ObserverView observer, DeltaTModel model,
        MeteorCatalogue? meteors = null, CancellationToken cancellation = default)
    {
        JulianInstant from = observer.Epoch;
        var ephemeris = world.Ephemeris;
        if (from.Scale != TimeScale.Tdb || from.CompareTo(ephemeris.Start) < 0 || from.CompareTo(ephemeris.End) >= 0)
            return [];
        double available = (double)ephemeris.End.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
        var end = from.AddSeconds(Math.Min(45 * JulianInstant.SecondsPerDay, available));
        var found = SolarSystemEventSearch.Find(ephemeris, from, end, cancellation)
            .Select(item => new AlmanacEntry(item.Epoch, item.Target, Description(item))).ToList();
        var site = (observer.EarthObserver ?? EarthObserverCamera.Moscow).Site;
        foreach (var solar in ObservationEventSearch.FindSolarEclipses(ephemeris, site, model, from, end, cancellation))
            found.Add(new(solar.Partial.Peak, SolarSystemSample.SunId, $"{solar.Class} solar eclipse · local site"));
        foreach (var lunar in ObservationEventSearch.FindLunarEclipses(ephemeris, from, end, cancellation))
            if (new[] { lunar.Penumbral.First, lunar.Penumbral.Peak, lunar.Penumbral.Last }.Any(Visible))
                found.Add(new(lunar.Penumbral.Peak, SolarSystemSample.MoonId, $"{lunar.Class} lunar eclipse · Moon above local horizon"));

        var shadowEnd = from.AddSeconds(Math.Min(3 * JulianInstant.SecondsPerDay, available));
        var jupiter = new ObjectId("sol/jupiter");
        var shadows = new List<AlmanacEntry>();
        foreach (var moon in NaturalBodyCatalogue.Bodies.Where(body => body.NaifId is >= 501 and <= 504))
        {
            cancellation.ThrowIfCancellationRequested();
            var intervals = ObservationEventSearch.FindShadowTransits(epoch =>
            {
                var at = world.AlmanacGraph.Evaluate(epoch, model);
                return at.TryState(SolarSystemSample.SunId, out var sun) && at.TryState(jupiter, out var receiver) &&
                    at.TryState(moon.Id, out var occluder)
                    ? (sun.PositionKilometers, receiver.PositionKilometers, occluder.PositionKilometers) : null;
            }, world.TargetRadiusKilometers(jupiter), moon.RadiusKilometers, from, shadowEnd, cancellation);
            if (intervals.Count > 0)
                shadows.Add(new(intervals[0].Peak, jupiter, $"{moon.Name} shadow on Jupiter · global spherical model"));
        }
        if (shadows.MinBy(item => item.Epoch) is { } firstShadow) found.Add(firstShadow);
        if (meteors is not null)
        {
            // Include today's peak until the end of its displayed civil day, even after its nominal maximum.
            var cursor = CivilCalendarDisplay.CivilDay(from, model) is { } today ? Midnight(today, model) : from;
            if (cursor.CompareTo(ephemeris.Start) < 0) cursor = ephemeris.Start;
            for (int count = 0; count < 64; count++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (meteors.NextPeak(ephemeris, cursor, includeOutbursts: true) is not { } peak || peak.Epoch.CompareTo(end) > 0)
                    break;
                if (CivilCalendarDisplay.CivilDay(peak.Epoch, model) is { } date &&
                    CivilCalendarDisplay.CivilDay(from, model) is { } now && date.DayNumber >= now.DayNumber)
                    found.Add(new(Midnight(date, model), null, $"{peak.Shower.Name} meteor maximum · date only", true));
                cursor = peak.Epoch.AddSeconds(60);
                if (cursor.CompareTo(end) >= 0) break;
            }
        }
        return found.OrderBy(item => item.Epoch).ToArray();

        bool Visible(JulianInstant? time) => time is { } t &&
            EarthSurfaceLighting.Sample(ephemeris.Sample(t), site, model).LunarAltitudeRadians > 0;
        string Name(ObjectId id) => world.Catalogue.Find(id)?.Name ?? id.Value;
        string Description(SolarSystemEvent item) => item.Kind switch
        {
            SolarSystemEventKind.NewMoon => "New Moon",
            SolarSystemEventKind.FirstQuarter => "First quarter",
            SolarSystemEventKind.FullMoon => "Full Moon",
            SolarSystemEventKind.LastQuarter => "Last quarter",
            SolarSystemEventKind.Opposition => Name(item.Target) + " opposition",
            SolarSystemEventKind.GreatestElongation => FormattableString.Invariant($"{Name(item.Target)} greatest elongation · {item.Degrees:F2}°"),
            SolarSystemEventKind.Season => item.Name ?? "Season",
            _ => FormattableString.Invariant($"{Name(item.Target)} / {item.Name ?? (item.Other is { } other ? Name(other) : "target")} approach · {item.Degrees:F3}°")
        };
    }

    /// <summary>Civil midnight, respecting leap seconds and the selected modeled historical UT1.</summary>
    public static JulianInstant Midnight(HistoricalDate date, DeltaTModel model) => date.Year >= 1972
        ? TimeConversion.FromUtc(new(date.ToGregorian(), 0, 0, 0), TimeScale.Tdb)
        : EarthCivilTime.FromModeledUt1(date, 0, model);
}
