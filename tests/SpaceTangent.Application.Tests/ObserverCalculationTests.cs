// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverCalculationTests
{
    [CalculationFact]
    [Trait("Category", "ExternalData")]
    public void EquatorialEquinoxProducesRealDailyAnnualAndMoonSeries()
    {
        var world = World();
        var epoch = TimeConversion.FromUtc(new(new(2024, 3, 20), 12), TimeScale.Tdb);
        var observer = new ObserverView(epoch, CameraMode.Surface, SolarSystemSample.SunId, false)
            { EarthObserver = new(new(0, 0, 0), 180, 30) };
        var result = ObserverCalculations.Calculate(world, observer, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal(30, result.Days.Count);
        Assert.Equal(366, result.Year.Count);
        Assert.Equal(121, result.MoonAngles.Count);
        var day = result.Days[0].Day;
        Assert.True(day.CompleteCoverage);
        Assert.Equal(97, day.Samples.Count);
        var rise = TimeConversion.ToUtc(day.Rise!.Value);
        Assert.InRange(rise.Hour + rise.Minute / 60.0, 5.9, 6.2);
        var highest = TimeConversion.ToUtc(day.Maximum!.Value.Epoch);
        Assert.InRange(highest.Hour + highest.Minute / 60.0, 12, 12.25);
        Assert.InRange(day.Maximum.Value.AltitudeDegrees!.Value, 89.5, 90);
        Assert.All(result.Year, row => Assert.InRange(row.Midnight.AltitudeDegrees!.Value, -90, -65));
        Assert.Contains(result.MoonAngles, row => row.MoonSeparationDegrees is > 175);
        Assert.Contains(result.MoonAngles, row => row.MoonSeparationDegrees is < 10);
        using var csv = new StringWriter(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        ObserverCalculations.WriteCsv(csv, result);
        var lines = csv.ToString().Trim().Split('\n');
        Assert.Equal(31, lines.Length);
        Assert.All(lines.Skip(1), line => Assert.Equal(8, line.Trim().Split(',').Length));
        Assert.Throws<OperationCanceledException>(() => ObserverCalculations.Calculate(world, observer,
            DeltaTModel.FixedOffset, new(true)));
    }

    [CalculationFact]
    [Trait("Category", "ExternalData")]
    public void LondonSpringDayUsesCivilMidnightsAndSelfMoonAnglesRemainZero()
    {
        var observer = new ObserverView(TimeConversion.FromUtc(new(new(2024, 3, 31), 12), TimeScale.Tdb),
            CameraMode.Surface, SolarSystemSample.MoonId, false)
        {
            EarthObserver = new(new(51.5, 0, 0), 180, 30)
                { CivilClock = new("London", "Europe/London", 0) }
        };
        var result = ObserverCalculations.Calculate(World(), observer, DeltaTModel.FixedOffset);
        var samples = result.Days[0].Day.Samples;
        Assert.InRange((double)samples[^1].Epoch.DifferenceNanoseconds(samples[0].Epoch) /
            JulianInstant.NanosecondsPerSecond, 82799.99, 82800.01);
        Assert.All(result.MoonAngles, row => Assert.InRange(row.MoonSeparationDegrees!.Value, 0, .000002));
    }

    private static SimulationWorld World() => new(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
    private sealed class CalculationFactAttribute : FactAttribute
    {
        public CalculationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set SPACETANGENT_DE440 to the public ephemeris.";
        }
    }
}
