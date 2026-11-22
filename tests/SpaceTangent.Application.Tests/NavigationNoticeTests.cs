// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NavigationNoticeTests
{
    [ScienceFact]
    [Trait("Category", "ExternalData")]
    public void FailedCenteringDoesNotCommitTheFallbackEpochButSuccessfulFlightReportsIt()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        var satellite = ScienceSatelliteCatalog.Load(Path.Combine(root, "catalog/sciencesat.json"), root)[0];
        var world = new SimulationWorld(new SharedClockTests.StaticEphemeris(), [satellite]);
        var epoch = satellite.Epoch.AddSeconds(500 * 86400d);
        var session = new SimulationSession(world, epoch, DeltaTModel.FixedOffset);
        Assert.True(world.Graph.Evaluate(satellite.Epoch, session.DeltaT).TryState(satellite.Id, out var target));
        var camera = new NavigationCamera(target.PositionKilometers, 1, 0, 0, null, false)
            { Placement = new(default, new(1, 0, 0), new(0, 0, 1)) };
        var view = session[0] with { Selection = satellite.Id, Navigation = camera };
        session.RestoreScenario(new([view, view with { Places = view.Places.ForkIdentity() }], true, 0, 0, session.DeltaT));
        var before = session.CaptureScenario(0);
        Assert.False(session.FocusSelection(0));
        Assert.Equal(before.Views.ToArray(), session.CaptureScenario(0).Views.ToArray());
        Assert.Null(session.LastNavigationEpochChange);
        Assert.True(session.GoToSelection(0));
        Assert.All(session.CaptureScenario(0).Views, item => Assert.Equal(satellite.Epoch, item.Epoch));
        var notice = Assert.IsType<SimulationSession.NavigationEpochChange>(session.LastNavigationEpochChange);
        Assert.True(notice.Shared);
        Assert.Equal(satellite.Id, notice.Target);
        Assert.Equal(epoch, notice.Before);
    }

    [Fact]
    public void MissingCentersAreDistinguishedFromUnavailableBodies()
    {
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        Assert.Null(PlanetEphemerides.CenterAt(null, SolarSystemSample.EarthId, epoch));
        Assert.True(PlanetEphemerides.CenterAt(null, PlanetCatalogue.SaturnId, epoch)!.ApproximateBarycenter);
        Assert.True(PlanetEphemerides.CenterAt(null, NaturalBodyCatalogue.PlutoId, epoch)!.ApproximateBarycenter);
        var charon = NaturalBodyCatalogue.Bodies.Single(body => body.NaifId == 901);
        Assert.False(PlanetEphemerides.CenterAt(null, charon.Id, epoch)!.ApproximateBarycenter);
    }

    [CenterKernelFact]
    [Trait("Category", "ExternalData")]
    public void InstalledCenterKernelIsPreciseOnlyInsideItsCoverage()
    {
        var root = Directory.CreateTempSubdirectory("st-center-coverage-");
        try
        {
            File.Copy(Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_CENTER")!,
                Path.Combine(root.FullName, "center-6.bsp"));
            var source = new PlanetEphemerides(root.FullName);
            Assert.Null(PlanetEphemerides.CenterAt(source, PlanetCatalogue.SaturnId,
                JulianInstant.FromJulianDate(2453182.5, TimeScale.Tdb)));
            Assert.NotNull(PlanetEphemerides.CenterAt(source, PlanetCatalogue.SaturnId,
                JulianInstant.FromJulianDate(1, TimeScale.Tdb)));
        }
        finally { root.Delete(true); }
    }

    private sealed class CenterKernelFactAttribute : FactAttribute
    {
        public CenterKernelFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_CENTER")))
                Skip = "Set a real Saturn centre kernel.";
        }
    }

    private sealed class ScienceFactAttribute : FactAttribute
    {
        public ScienceFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set the original science catalogue root.";
        }
    }
}
