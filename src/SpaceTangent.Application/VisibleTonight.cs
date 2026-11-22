// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record TonightTarget(ObjectId Id, string Name, CatalogueKind Kind, double? Magnitude,
    HorizontalCoordinates BestDirection, JulianInstant Best, JulianInstant Above, JulianInstant Below);
public sealed record TonightResult(HistoricalDate Date, NightVisibility Visibility, double? SunLimitDegrees,
    JulianInstant? From, JulianInstant? To, IReadOnlyList<TonightTarget> Targets);

/// <summary>The installed planets, named bright stars and magnitude-qualified Messier objects visible in a sampled night.</summary>
public static class VisibleTonight
{
    private static readonly Regex Messier = new(@"^M\s*([0-9]{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private sealed record Candidate(ObjectId Id, string Name, CatalogueKind Kind, double? Magnitude,
        Func<JulianInstant, Vector3d>? Direction = null, Vector3d? Position = null);

    public static TonightResult Calculate(SimulationWorld world, ObserverView observer, DeltaTModel model,
        CancellationToken cancellation = default)
    {
        var camera = observer.EarthObserver ?? EarthObserverCamera.Default;
        SurfaceObserver.RequireCalculationFrame(world, camera, observer.Epoch, model);
        var clock = camera.IsEarth ? camera.CivilClock.GetValueOrDefault() : new ObserverSiteClock("UTC", "Etc/UTC", 0);
        var date = clock.DateAt(observer.Epoch, camera.Site, model);
        var noon = clock.At(date, new(12, 0), camera.Site, model);
        if (observer.Epoch.CompareTo(noon) < 0)
        { date = date.AddDays(-1); noon = clock.At(date, new(12, 0), camera.Site, model); }
        var end = clock.At(date.AddDays(1), new(12, 0), camera.Site, model);
        var civil = HistoricalDate.FromGregorian(date);
        var cache = new Dictionary<JulianInstant, (SolarSystemSample Solar, BodySurfaceFrame Frame)?>();
        double? depth = null;
        JulianInstant? from = null, to = null;
        foreach (double limit in new[] { -12d, -6, -.833 })
        {
            double effective = camera.IsEarth || limit != -.833 ? limit : 0;
            var day = ObserverDaySearch.Find(epoch => At(epoch) is { } at
                ? new(epoch, at.Frame.Horizontal(at.Solar.Sun.PositionKilometers)?.AltitudeDegrees, null, null)
                : new(epoch, null, null, null), noon, end, effective, cancellation);
            if (!day.CompleteCoverage) return new(civil, NightVisibility.NoEphemeris, null, null, null, []);
            var intervals = new List<(JulianInstant From, JulianInstant To)>();
            JulianInstant? darkStart = day.StartsAbove == false ? noon : null;
            foreach (var crossing in day.Crossings)
            {
                if (crossing.Rising)
                {
                    if (darkStart is { } beginning && crossing.Epoch.CompareTo(beginning) > 0)
                        intervals.Add((beginning, crossing.Epoch));
                    darkStart = null;
                }
                else darkStart = crossing.Epoch;
            }
            if (darkStart is { } last && end.CompareTo(last) > 0) intervals.Add((last, end));
            if (intervals.Count > 0)
            {
                var longest = intervals.MaxBy(interval => interval.To.DifferenceNanoseconds(interval.From));
                from = longest.From; to = longest.To; depth = effective; break;
            }
        }
        if (from is not { } start || to is not { } finish) return new(civil, NightVisibility.NoDarkness, null, null, null, []);
        var candidates = PlanetCatalogue.Planets.Where(body => body.Id != camera.SurfaceBody)
            .Select(body => new Candidate(body.Id, body.Name, CatalogueKind.Planet, null)).ToList();
        if (camera.SurfaceBody != SolarSystemSample.MoonId) candidates.Add(new(SolarSystemSample.MoonId, "Moon", CatalogueKind.Moon, null));
        foreach (var star in world.BrightStars.Stars.Where(star => star.VisualMagnitude <= 1.5 && star.Name.Length > 0))
            candidates.Add(new(star.Id, star.Name, CatalogueKind.Star, star.VisualMagnitude, star.DirectionAt));
        foreach (var item in world.DeepSky.Objects)
        {
            cancellation.ThrowIfCancellationRequested();
            string? name = new[] { item.Designation, item.Name }.Concat(item.Aliases).FirstOrDefault(text => Messier.IsMatch(text));
            if (name is null || item.AbsoluteMagnitude is not { } absolute || !(item.DistanceLightYears > 0)) continue;
            int number = int.Parse(Messier.Match(name).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (number is < 1 or > 110) continue;
            double magnitude = absolute + 5 * Math.Log10(item.DistanceLightYears / 3.26156) - 5;
            if (magnitude <= 8) candidates.Add(new(item.Id, $"M {number}", item.Kind == DeepSkyKind.Galaxy ? CatalogueKind.Galaxy :
                item.Kind == DeepSkyKind.Nebula ? CatalogueKind.Nebula : CatalogueKind.Cluster,
                magnitude, Position: item.PositionKilometers + world.StellarOrigin));
        }
        var results = new Dictionary<ObjectId, TonightTarget>();
        double duration = (double)finish.DifferenceNanoseconds(start) / JulianInstant.NanosecondsPerSecond;
        for (double seconds = 0; seconds < duration + 600; seconds += 600)
        {
            cancellation.ThrowIfCancellationRequested();
            var epoch = start.AddSeconds(Math.Min(seconds, duration));
            if (At(epoch) is not { } at) continue;
            var snapshot = world.AlmanacGraph.Evaluate(epoch, model);
            foreach (var target in candidates)
            {
                double? magnitude = target.Magnitude;
                Vector3d? direction = target.Direction?.Invoke(epoch) ?? (target.Position - at.Frame.Observer.PositionKilometers);
                if (direction is null && snapshot.TryState(target.Id, out var state))
                {
                    direction = state.PositionKilometers - at.Frame.Observer.PositionKilometers;
                    magnitude = ObserverPhotometry.Estimate(world, target.Id, state, at.Frame.Observer.PositionKilometers, at.Solar.Sun);
                }
                if (direction is not { } ray || at.Frame.Direction(ray) is not { } geometric) continue;
                var sky = geometric with { AltitudeDegrees = camera.IsEarth && camera.Conditions is { } air ?
                    air.ApparentAltitude(geometric.AltitudeDegrees, camera.Site) : geometric.AltitudeDegrees };
                if (sky.AltitudeDegrees < 10) continue;
                if (!results.TryGetValue(target.Id, out var old))
                    results.Add(target.Id, new(target.Id, target.Name, target.Kind, magnitude, sky, epoch, epoch, epoch));
                else results[target.Id] = sky.AltitudeDegrees > old.BestDirection.AltitudeDegrees
                    ? old with { Best = epoch, BestDirection = sky, Magnitude = magnitude, Below = epoch }
                    : old with { Below = epoch };
            }
            if (seconds >= duration) break;
        }
        var ordered = results.Values.OrderBy(item => item.Best).ThenBy(item => item.Magnitude).ToArray();
        return new(civil, ordered.Length > 0 ? NightVisibility.Visible : NightVisibility.BelowAltitude, depth, start, finish, ordered);

        (SolarSystemSample Solar, BodySurfaceFrame Frame)? At(JulianInstant epoch)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cache.TryGetValue(epoch, out var value)) return value;
            if (epoch.CompareTo(world.Ephemeris.Start) < 0 || epoch.CompareTo(world.Ephemeris.End) > 0) return null;
            var snapshot = world.AlmanacGraph.Evaluate(epoch, model);
            value = SurfaceObserver.TryFrame(world, camera, snapshot, model, out var frame) && snapshot.SolarSystem is { } solar ? (solar, frame) : null;
            if (cache.Count < 2048) cache.Add(epoch, value);
            return value;
        }
    }
}
