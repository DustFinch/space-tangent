// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class WorldReplacementAtomicityTests
{
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
