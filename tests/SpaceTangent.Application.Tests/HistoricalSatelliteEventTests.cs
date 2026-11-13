// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class HistoricalSatelliteEventTests
{
    [HistoricalTleFact]
    [Trait("Category", "ExternalData")]
    public void SearchesAndTargetsTheOriginalHistoricalFlare()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var satellites = LocalTleCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_IRIDIUM_ROOT")!);
        var world = new SimulationWorld(ephemeris, [], localSatellites: satellites);
        var start = JulianInstant.FromJulianDate(2457547.0 + 68.184 / 86400, TimeScale.Tdb);
        var end = start.AddSeconds(2 * JulianInstant.SecondsPerDay);
        const DeltaTModel model = DeltaTModel.MorrisonStephenson2004;
        var site = EarthObserverCamera.Moscow;
        var expected = satellites.SelectMany(satellite => ObservationEventSearch.FindSatelliteFlares(
            ephemeris, satellite, site.Site, model, start, end).Select(flare => (satellite.Id, Flare: flare)))
            .OrderBy(item => item.Flare.Peak).First();
        var observer = new ObserverView(start, CameraMode.Surface, expected.Id, false) { EarthObserver = site };
        var found = ObserverEventFinder.Next(world, observer, model);
        Assert.NotNull(found);
        Assert.Equal(expected.Id, found.Target);
        Assert.Equal(expected.Flare.Peak, found.Epoch);
        Assert.Contains("flare", found.Description, StringComparison.Ordinal);
        var firstInGroup = ObserverEventFinder.Next(world, observer with { Selection = null }, model);
        Assert.NotNull(firstInGroup);
        Assert.Equal(expected.Id, firstInGroup.Target);
        Assert.Equal(expected.Flare.Peak, firstInGroup.Epoch);
        Assert.Equal(expected.Flare.Horizontal, firstInGroup.FlareDirection);
    }

    private sealed class HistoricalTleFactAttribute : FactAttribute
    {
        public HistoricalTleFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_IRIDIUM_ROOT")))
                Skip = "Set the original June 2016 TLE archive directory and the DE440 ephemeris.";
        }
    }
}
