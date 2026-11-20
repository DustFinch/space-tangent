// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NearbyAlmanacTests
{
    [Fact]
    public void CivilMidnightRetainsLeapSecondsAndHistoricalCalendar()
    {
        var before = NearbyAlmanac.Midnight(new(2016, 12, 31), DeltaTModel.FixedOffset);
        var after = NearbyAlmanac.Midnight(new(2017, 1, 1), DeltaTModel.FixedOffset);
        Assert.Equal(TimeScale.Tdb, before.Scale);
        Assert.InRange((double)after.DifferenceNanoseconds(before) / JulianInstant.NanosecondsPerSecond, 86400.99, 86401.01);
        var ancient = new HistoricalDate(1054, 7, 4);
        Assert.Equal(ancient, CivilCalendarDisplay.CivilDay(NearbyAlmanac.Midnight(ancient,
            DeltaTModel.MorrisonStephenson2004), DeltaTModel.MorrisonStephenson2004));
    }

    [AlmanacFact]
    [Trait("Category", "ExternalData")]
    public void RealEphemerisProducesOrderedPhasesOppositionsAndJovianShadow()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var world = new SimulationWorld(ephemeris, []);
        var observer = new ObserverView(TimeConversion.FromUtc(new(new(2024, 10, 16)), TimeScale.Tdb),
            CameraMode.Surface, SolarSystemSample.MoonId, false) { EarthObserver = EarthObserverCamera.Moscow };
        var results = NearbyAlmanac.Find(world, observer, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal(results.OrderBy(item => item.Epoch), results);
        Assert.Contains(results, item => item.Description == "Full Moon");
        Assert.Contains(results, item => item.Description == "Uranus opposition");
        Assert.Single(results, item => item.Description.Contains("shadow on Jupiter", StringComparison.Ordinal));
        Assert.All(results, item => Assert.InRange((double)item.Epoch.DifferenceNanoseconds(observer.Epoch) /
            JulianInstant.NanosecondsPerDay, 0, 45));
        Assert.Throws<OperationCanceledException>(() => NearbyAlmanac.Find(world, observer,
            DeltaTModel.FixedOffset, cancellation: new(true)));
    }

    private sealed class AlmanacFactAttribute : FactAttribute
    {
        public AlmanacFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set SPACETANGENT_DE440 to the public ephemeris.";
        }
    }
}
