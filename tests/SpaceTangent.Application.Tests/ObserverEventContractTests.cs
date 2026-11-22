// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverEventContractTests
{
    [Fact]
    public void LocalTleSelectionReturnsAPassInsideTheTwoDaySearchWindow()
    {
        var directory=Directory.CreateTempSubdirectory("st-local-pass-");
        try
        {
            // Original Jason-1 public TLE, also retained by the science catalogue regression.
            File.WriteAllText(Path.Combine(directory.FullName,"jason.tle"),"""
                JASON-1
                1 26997U 01055A   11192.81984987 -.00000038  00000-0  10000-3 0  3699
                2 26997  66.0463 303.1838 0007562 261.9351  98.0768 12.80928955448691
                """);
            var satellite=Assert.Single(SpaceTangent.Core.Addons.LocalTleCatalogue.Load(directory.FullName));
            var world=new SimulationWorld(new SharedClockTests.StaticEphemeris(),[],localSatellites:[satellite]);
            var observer=new ObserverView(satellite.Epoch,CameraMode.Surface,satellite.Id,false);
            var found=Assert.IsType<ObserverEvent>(ObserverEventFinder.Next(world,observer,DeltaTModel.MorrisonStephenson2004));
            Assert.Equal(satellite.Id,found.Target); Assert.Contains("JASON-1 pass",found.Description);
            Assert.True(found.Epoch.CompareTo(satellite.Epoch)>=0&&found.Epoch.CompareTo(satellite.Epoch.AddSeconds(2*86400))<=0);
            Assert.Null(found.FlareDirection);
        }
        finally {directory.Delete(recursive:true);}
    }

    [Fact]
    public void AnAnalyticalPlanetConjunctionIncludesTheRetardedEmissionTime()
    {
        var ephemeris = new ConjunctionEphemeris(); var world = new SimulationWorld(ephemeris, []);
        var observer = new ObserverView(new(2451545,0,TimeScale.Tdb), CameraMode.Solar, PlanetCatalogue.MarsId, false);
        var found = Assert.IsType<ObserverEvent>(ObserverEventFinder.Next(world, observer, DeltaTModel.FixedOffset));
        Assert.Equal(PlanetCatalogue.MarsId, found.Target); Assert.Contains("Mars and Jupiter conjunction", found.Description);
        // Mars crosses the fixed Jupiter direction after one day at emission. Both are exactly 1e8 km away.
        double expected = 86400 + 1e8 / 299792.458;
        double seconds = (double)found.Epoch.DifferenceNanoseconds(observer.Epoch) / JulianInstant.NanosecondsPerSecond;
        Assert.InRange(Math.Abs(seconds-expected), 0, 1);
        Assert.Null(found.FlareDirection);
    }

    [Fact]
    public void SearchCancellationAndTheCoverageEndCannotReturnPartialEvents()
    {
        var ephemeris = new ConjunctionEphemeris(); var world = new SimulationWorld(ephemeris, []);
        var observer = new ObserverView(new(2451545,0,TimeScale.Tdb), CameraMode.Solar, PlanetCatalogue.MarsId, false);
        Assert.Throws<OperationCanceledException>(() => ObserverEventFinder.Next(world, observer, DeltaTModel.FixedOffset,
            new CancellationToken(true)));
        Assert.Null(ObserverEventFinder.Next(world, observer with { Epoch=ephemeris.End }, DeltaTModel.FixedOffset));
    }

    private sealed class ConjunctionEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(2451544,0,TimeScale.Tdb);
        public JulianInstant End => new(2451548,0,TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch)
        {
            double days = (double)epoch.DifferenceNanoseconds(new(2451545,0,TimeScale.Tdb))/JulianInstant.NanosecondsPerSecond/86400;
            return new(epoch, new(new(-1.5e8,0,0),default), default, new(new(384400,0,0),default), Polar(1))
            {
                Mercury=Polar(.6), Venus=Polar(.8), MarsBarycenter=Polar((days-1)*.05), JupiterBarycenter=Polar(0),
                UranusBarycenter=Polar(-.6), NeptuneBarycenter=Polar(-.8), PlutoBarycenter=Polar(-1)
            };
        }
        private static CartesianState Polar(double angle) => new(new(1e8*Math.Cos(angle),1e8*Math.Sin(angle),0),default);
    }
}
