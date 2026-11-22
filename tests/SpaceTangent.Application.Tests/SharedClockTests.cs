// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SharedClockTests
{
    [Theory]
    [InlineData(600d, 1d, 0)]
    [InlineData(600d, 1d, 1)]
    [InlineData(null, 1d, 0)]
    [InlineData(-600d, 10d, 0)]
    public void JoiningIndependentViewsKeepsTheSourceRateAcrossClockOwners(double? first, double? second, int source)
    {
        var session = Create();
        var view = session[0];
        session.RestoreScenario(new([view with { PlaybackRate = first }, view with { PlaybackRate = second, Places = view.Places.ForkIdentity() }],
            false, 0, 0, session.DeltaT));
        double expected = session.CurrentPlaybackRate(source);
        session.Synchronize(true, source);
        for (int index = 0; index < 2; index++)
        {
            session.Step(index, 3600);
            session.SetPlaying(index, false);
            session.SetMode(index, CameraMode.Surface);
            Assert.Equal(expected, session.CurrentPlaybackRate(index));
        }
        var saved = ScenarioFile.Decode(ScenarioFile.Encode(session.CaptureScenario(1)));
        session.RestoreScenario(saved);
        Assert.True(session.TryReplaceWorld(new(new StaticEphemeris(), [])));
        session.ReversePlayback(1);
        Assert.Equal(-expected, session.CurrentPlaybackRate(0));
        session.Synchronize(false, 0);
        Assert.Equal(session.CurrentPlaybackRate(0), session.CurrentPlaybackRate(1));
    }

    [Fact]
    public void LegacySynchronizedSceneUsesItsClockOwnersRate()
    {
        var session = Create();
        var view = session[0];
        session.RestoreScenario(new([view with { PlaybackRate = 600 }, view with { PlaybackRate = 1, Places = view.Places.ForkIdentity() }],
            true, 0, 0, session.DeltaT));
        session.Step(1, 3600);
        Assert.Equal(600, session.CurrentPlaybackRate(0));
        Assert.All(session.CaptureScenario(0).Views, item => Assert.Equal(600, item.PlaybackRate));
    }

    [Theory]
    [InlineData(2106215d, DeltaTModel.FixedOffset)]
    [InlineData(2451545d, DeltaTModel.MorrisonStephenson2004)]
    [InlineData(2816795d, DeltaTModel.EspenakMeeus2006)]
    public void SurfaceSiteVelocityUsesEraSpinInTheSelectedEarthFrame(double day, DeltaTModel model)
    {
        var world = new SimulationWorld(new StaticEphemeris(), [], locations:
            [new SscSurfaceLocation("Station", "Sol/Earth", 30, 55, .15, 1, null, null)]);
        var site = Assert.Single(world.SurfaceSitesOn(SolarSystemSample.EarthId));
        var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
        var snapshot = world.Graph.Evaluate(epoch, model);
        Assert.True(world.TryNavigationState(site.Id, snapshot, out var state, model));
        var axes = EarthOrientation.TerrestrialToWorld(epoch, model).ToWorld;
        // IAU Earth rotation angle rate in rad/s, independent of the former sidereal-day approximation.
        Vector3d expected = Vector3d.Cross(axes.Z * 7.292115146706979e-5, axes.Transform(site.OffsetKilometers));
        Assert.InRange((state.VelocityKilometersPerSecond - expected).Length, 0, 1e-12);
    }

    internal static SimulationSession Create() => new(new(new StaticEphemeris(), []),
        JulianInstant.FromJulianDate(2451545, TimeScale.Tdb), DeltaTModel.MorrisonStephenson2004);

    internal sealed class StaticEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}
