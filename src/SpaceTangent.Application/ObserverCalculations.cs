// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record ObserverTableDay(HistoricalDate Date, ObserverDay Day);
public sealed record ObserverAnnualSample(HistoricalDate Date, ObserverChartSample Midnight, bool? VisibleAtNight);
public sealed record ObserverCalculationResult(ObjectId Target, string Name, HistoricalDate FirstDate,
    IReadOnlyList<ObserverTableDay> Days, IReadOnlyList<ObserverAnnualSample> Year,
    IReadOnlyList<ObserverChartSample> MoonAngles, HistoricalDate? BestNight, double? BestNightAltitude);

/// <summary>Captured observer calculations; no session mutation or background execution of add-on scripts.</summary>
public static class ObserverCalculations
{
    public static ObserverCalculationResult Calculate(SimulationWorld world, ObserverView observer, DeltaTModel model,
        CancellationToken cancellation = default)
    {
        if (observer.Selection is not { } target || world.Catalogue.Find(target) is not { } item)
            throw new ArgumentException("Select a catalogue object before calculating ephemerides.", nameof(observer));
        var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
        if (target == camera.SurfaceBody) throw new ArgumentException("Select an object other than the observer body.", nameof(observer));
        var brightStar = world.BrightStars.Find(target);
        var spatialStar = world.FindStellarTarget(target);
        var clock = camera.IsEarth ? camera.CivilClock.GetValueOrDefault() : new ObserverSiteClock("UTC", "Etc/UTC", 0);
        var date = HistoricalDate.FromGregorian(clock.DateAt(observer.Epoch, camera.Site, model));
        var yearStart = new HistoricalDate(date.Year, 1, 1);
        var yearEnd = new HistoricalDate(date.Year + 1, 1, 1);
        var days = new List<ObserverTableDay>();
        var year = new List<ObserverAnnualSample>();
        var angles = new List<ObserverChartSample>();
        HistoricalDate? bestNight = null;
        double? bestAltitude = null;
        for (int i = 0; i < 30; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var day = HistoricalDate.FromDayNumber(date.DayNumber + i);
            var start = At(day, 0);
            var end = At(HistoricalDate.FromDayNumber(day.DayNumber + 1), 0);
            // Standard level-horizon refraction; add angular semidiameter for Sun/Moon only.
            double limit = !camera.IsEarth ? 0 : target == SolarSystemSample.SunId || target == SolarSystemSample.MoonId ? -.833 : -.5667;
            days.Add(new(day, ObserverDaySearch.Find(Sample, start, end, limit, cancellation)));
        }
        for (long day = yearStart.DayNumber; day < yearEnd.DayNumber; day++)
        {
            cancellation.ThrowIfCancellationRequested();
            var civil = HistoricalDate.FromDayNumber(day);
            var midnight = Sample(At(civil, 0));
            var evening = At(civil, 18);
            int covered = 0;
            bool visible = false;
            for (int step = 0; step <= 28; step++)
            {
                var sample = Sample(evening.AddSeconds(step * 1800));
                if (sample.AltitudeDegrees is not { } altitude || sample.SunAltitudeDegrees is not { } sun) continue;
                covered++;
                if (sun > -12 || altitude < 10) continue;
                visible = true;
                if (step == 12 && (bestAltitude is null || altitude > bestAltitude))
                { bestNight = civil; bestAltitude = altitude; }
            }
            year.Add(new(civil, midnight, covered == 0 ? null : visible));
        }
        var first = At(date, 0);
        for (int i = 0; i <= 120; i++) angles.Add(Sample(first.AddSeconds(i * 21600)));
        return new(target, item.Name, date, days.AsReadOnly(), year.AsReadOnly(), angles.AsReadOnly(), bestNight, bestAltitude);

        JulianInstant At(HistoricalDate day, int hour) => clock.At(day.ToGregorian(), new(hour, 0), camera.Site, model);
        ObserverChartSample Sample(JulianInstant epoch)
        {
            cancellation.ThrowIfCancellationRequested();
            if (epoch.CompareTo(world.Ephemeris.Start) < 0 || epoch.CompareTo(world.Ephemeris.End) > 0)
                return new(epoch, null, null, null);
            var solar = world.Ephemeris.Sample(epoch);
            BodySurfaceFrame frame;
            if (camera.IsEarth)
            {
                var light = EarthSurfaceLighting.Sample(solar, camera.Site, model);
                frame = new(solar.Earth + new CartesianState(light.PositionKilometers, default), light.EastNorthUp);
            }
            else if (!SurfaceObserver.TryFrame(world, camera, world.AlmanacGraph.Evaluate(epoch, model), model, out frame))
                return new(epoch, null, null, null);
            var eye = frame.Observer.PositionKilometers;
            CartesianState? bodyState = target == SolarSystemSample.SunId ? solar.Sun :
                target == SolarSystemSample.MoonId ? solar.Moon : target == SolarSystemSample.EarthId ? solar.Earth : null;
            Vector3d? offset;
            if (spatialStar is not null) offset = spatialStar.StateAt(epoch).PositionKilometers + world.StellarOrigin - eye;
            else if (brightStar is not null) offset = brightStar.DirectionAt(epoch) * 1e14;
            else
            {
                if (bodyState is null && world.TryNavigationState(target, world.AlmanacGraph.Evaluate(epoch, model), out var state, model))
                    bodyState = state;
                offset = bodyState?.PositionKilometers - eye;
            }
            var horizontal = offset is { } direction ? frame.Direction(direction) : null;
            var moon = solar.Moon.PositionKilometers - eye;
            var sun = solar.Sun.PositionKilometers - eye;
            double? magnitude = spatialStar?.VisualMagnitude ?? brightStar?.VisualMagnitude;
            if (magnitude is null && bodyState is { } body) magnitude = ObserverPhotometry.Estimate(world, target, body, eye, solar.Sun);
            return new(epoch, horizontal?.AltitudeDegrees, horizontal?.AzimuthDegrees,
                frame.Direction(sun)?.AltitudeDegrees, camera.SurfaceBody == SolarSystemSample.MoonId ? null : frame.Direction(moon)?.AltitudeDegrees,
                camera.SurfaceBody == SolarSystemSample.MoonId ? null : Angle(offset, moon), Angle(offset, sun), magnitude);
        }
    }

    public static void WriteCsv(TextWriter writer, ObserverCalculationResult result)
    {
        writer.WriteLine("civil_date,rise_jd_tdb,maximum_jd_tdb,maximum_altitude_deg,set_jd_tdb,estimated_magnitude,elongation_deg,complete_coverage");
        foreach (var row in result.Days)
        {
            var day = row.Day;
            writer.WriteLine(string.Join(',', row.Date.ToString(), Number(day.Rise?.ToJulianDate()),
                Number(day.Maximum?.Epoch.ToJulianDate()), Number(day.Maximum?.AltitudeDegrees), Number(day.Set?.ToJulianDate()),
                Number(day.Maximum?.Magnitude), Number(day.Maximum?.ElongationDegrees), day.CompleteCoverage ? "1" : "0"));
        }
        static string Number(double? value) => value is { } number && double.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture) : "";
    }

    private static double? Angle(Vector3d? first, Vector3d second) => first is { Length: > 0 } a && second.Length > 0
        ? Math.Acos(Math.Clamp(Vector3d.Dot(a / a.Length, second / second.Length), -1, 1)) * 180 / Math.PI : null;
}
