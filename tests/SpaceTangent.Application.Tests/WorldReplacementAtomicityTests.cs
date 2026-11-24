// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class WorldReplacementAtomicityTests
{
    [Fact]
    public void FailedPreferenceCommitRestoresLiveViewIdentitiesPlacesAndAnUnfinishedFlight()
    {
        var ephemeris = new SharedClockTests.StaticEphemeris();
        var id = new ObjectId("mission/rollback-target");
        var frame = new MissionFrame.Inertial(Basis3d.Identity);
        var mission = new SampledMission(id, "Rollback target", "fixture.glb", 1,
            [new(ephemeris.Start, ephemeris.End, SolarSystemSample.EarthId,
                _ => new CartesianState(new(10000, 2000, 0), default), frame, frame)]);
        var original = new SimulationWorld(ephemeris, [], sampledMissions: [mission]);
        var session = new SimulationSession(original, JulianInstant.FromJulianDate(2451545, TimeScale.Tdb),
            DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0); session.SelectObject(0, id);
        session.SetEarthObserver(0, EarthObserverCamera.Greenwich with { Body = SolarSystemSample.MoonId });
        Assert.True(session.GoToSelection(0)); Assert.True(session.IsFlying(0));
        int identity = session.ViewIdentity(0), version = session.ObserverPlacesVersion;
        byte[] before = ScenarioFile.Encode(session.CaptureScenario(0));
        var next = new SimulationWorld(ephemeris, []);
        int writes = 0;
        Assert.Throws<IOException>(() => session.TryReplaceWorld(next, () =>
        {
            writes++; Assert.Same(next, session.World); Assert.False(session.IsFlying(0));
            throw new IOException("Preferences could not be committed.");
        }));
        Assert.Equal(1, writes); Assert.Same(original, session.World);
        Assert.Equal(before, ScenarioFile.Encode(session.CaptureScenario(0)));
        Assert.Equal(identity, session.ViewIdentity(0)); Assert.Equal(version, session.ObserverPlacesVersion);
        Assert.True(session.IsFlying(0)); Assert.Empty(session.SurfaceObserverFallbacks);
    }

    [Fact]
    public void SuccessfulPreferenceCommitRunsOnceAgainstTheAppliedWorld()
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0); session.Step(1, 3600);
        byte[] before = ScenarioFile.Encode(session.CaptureScenario(1));
        var next = new SimulationWorld(new SharedClockTests.StaticEphemeris(), []); int writes = 0;
        Assert.True(session.TryReplaceWorld(next, () => { writes++; Assert.Same(next, session.World); }));
        Assert.Equal(1, writes); Assert.Same(next, session.World);
        Assert.Equal(before, ScenarioFile.Encode(session.CaptureScenario(1)));
    }

    [Fact]
    public void AnEvaluationFailureInALaterObserverLeavesTheEntireSessionUnchanged()
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0);
        session.Step(1, 3600);
        var site = EarthObserverCamera.Greenwich with { Body = SolarSystemSample.MoonId };
        session.SetEarthObserver(0, site); session.SetEarthObserver(1, site);
        var ephemeris = new FailingEphemeris();
        var next = new SimulationWorld(ephemeris, []);
        ephemeris.FailureEpoch = session[1].Epoch;
        var world = session.World;
        byte[] before = ScenarioFile.Encode(session.CaptureScenario(1));
        int version = session.ObserverPlacesVersion;
        Assert.Throws<InvalidDataException>(() => session.TryReplaceWorld(next));
        Assert.Same(world, session.World);
        Assert.Equal(before, ScenarioFile.Encode(session.CaptureScenario(1)));
        Assert.Equal(version, session.ObserverPlacesVersion);
        Assert.Empty(session.SurfaceObserverFallbacks);
    }

    private sealed class FailingEphemeris : ISolarSystemEphemeris
    {
        private readonly SharedClockTests.StaticEphemeris _source = new();
        public JulianInstant? FailureEpoch { get; set; }
        public JulianInstant Start => _source.Start;
        public JulianInstant End => _source.End;
        public SolarSystemSample Sample(JulianInstant epoch) => epoch == FailureEpoch
            ? throw new InvalidDataException("A replacement source failed at the second observer's epoch.") : _source.Sample(epoch);
    }
}
