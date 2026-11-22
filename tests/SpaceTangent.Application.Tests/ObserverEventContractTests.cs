// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverEventContractTests
{
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
