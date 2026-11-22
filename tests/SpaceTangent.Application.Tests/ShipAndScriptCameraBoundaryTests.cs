// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ShipAndScriptCameraBoundaryTests
{
    [Fact]
    public void UnboardedControlsAreNoOpsAndInvalidControlsCannotChangeTheObserver()
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        session.ToggleShipCockpit(0); session.SetShipThrottle(0, 10); session.FlyShipManually(0, 1, .1, .2, .3);
        Assert.False(session.SetShipPilot(0, ShipPilot.Manual)); Assert.Null(session.Ship(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetShipThrottle(0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.FlyShipManually(0, 0, 0, double.PositiveInfinity, 0));
        Assert.Equal(ScenarioFile.Encode(before), ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void ManualSteeringDisengagesThePilotAndKeepsABoundedPhysicalTrail()
    {
        var session = SharedClockTests.Create(); session.Split(); var other = session[1]; session.BoardShip(0);
        var boarded = session.Ship(0)!; session.BoardShip(0); Assert.Equal(boarded, session.Ship(0));
        session.FlyShipManually(0, -1, 0, 0, 0); Assert.Equal(0, session.Ship(0)!.SpeedKilometersPerSecond);
        session.FlyShipManually(0, 1, 0, 0, 0); Assert.Equal(1, session.Ship(0)!.SpeedKilometersPerSecond);
        session.FlyShipManually(0, 2, 0, 0, 0); Assert.Equal(100, session.Ship(0)!.SpeedKilometersPerSecond);
        session.SelectObject(0, SolarSystemSample.MoonId); Assert.True(session.SetShipPilot(0, ShipPilot.Approach));
        session.FlyShipManually(0, 0, .2, -.1, .3);
        var steered = session.Ship(0)!;
        Assert.Equal(ShipPilot.Manual, steered.Pilot); Assert.NotEqual(boarded.Forward, steered.Forward);
        Assert.InRange(Math.Abs(Vector3d.Dot(steered.Forward, steered.Up)), 0, 1e-12);
        Assert.True(session.AdjustCamera(0, .3, -.2, 10));
        Assert.True(session[0].Navigation!.Value.IsValid());
        session.SetShipThrottle(0, 10);
        var snapshot = session.World.Graph.Evaluate(session[0].Epoch);
        for (int i = 0; i < 300; i++) session.UpdateShip(0, snapshot, .25);
        var flown = session.Ship(0)!;
        Assert.Equal(256, flown.Trail.Count);
        // Three hundred binary64 position additions near 1e8 km accumulate centimetre-scale rounding.
        Assert.InRange((flown.Position - steered.Position).Length, 749.99995, 750.00005);
        Assert.Equal(other, session[1]); Assert.True(session.SetShipPilot(0, ShipPilot.Manual));
        Assert.False(session.SetShipPilot(0, (ShipPilot)999));
        Assert.True(session.ToggleTracking(0)); Assert.Equal(ShipPilot.Orbit, session.Ship(0)!.Pilot);
    }

    [Fact]
    public void ScriptPoseAndDollyUseTheAbsoluteFreeFlightEye()
    {
        var session = SharedClockTests.Create(); session.StartFreeFlight(0);
        var snapshot = session.World.Graph.Evaluate(session[0].Epoch);
        session.SelectObject(0, SolarSystemSample.EarthId);
        var eye = new Vector3d(25000, 0, 0);
        session.SetClassicPose(0, snapshot, eye, (new(0, 0, -1), new(0, 1, 0)));
        Assert.Equal(eye, session.ScriptObserverEye(0, snapshot));
        Assert.True(session.DollyScriptView(0, Math.Log(2), snapshot));
        Assert.InRange((session.ScriptObserverEye(0, snapshot) - eye * 2).Length, 0, 1e-10);
        Assert.True(session.MoveClassicView(0, new(1, 0, 0), snapshot));
        Assert.InRange((session.ScriptObserverEye(0, snapshot) - (eye * 2 + new Vector3d(1, 0, 0))).Length, 0, 1e-10);
        session.BoardShip(0);
        Assert.False(session.MoveClassicView(0, new(1, 0, 0), snapshot));
        Assert.False(session.DollyScriptView(0, 1, snapshot));
    }

    [Fact]
    public void SurfaceScriptAimUsesTheLocalHorizonAndLeavesTheSecondViewAlone()
    {
        var session = SharedClockTests.Create(); session.SetMode(0, CameraMode.Surface); session.Split(); var other = session[1];
        var snapshot = session.World.Graph.Evaluate(session[0].Epoch);
        var site = EarthObserverCamera.Moscow;
        var lighting = SpaceTangent.Core.Observing.EarthSurfaceLighting.Sample(snapshot.SolarSystem!.Value, site.Site, session.DeltaT);
        var expectedEye = snapshot.SolarSystem.Value.Earth.PositionKilometers + lighting.PositionKilometers;
        Assert.InRange((session.ScriptObserverEye(0, snapshot) - expectedEye).Length, 0, 1e-9);
        Assert.False(session.DollyScriptView(0, 1, snapshot));
        session.TurnScriptView(0, new(0, 1, 0), .1, false, snapshot);
        Assert.NotEqual(site.AzimuthDegrees, session[0].EarthObserver!.Value.AzimuthDegrees);
        session.LookBackForScript(0, snapshot); Assert.Null(session[0].Navigation);
        session.SetScriptAimTarget(0, SolarSystemSample.MoonId); session.UpdateCamera(0, snapshot, 0);
        var expected = lighting.EastNorthUp.Transpose().Transform(snapshot.SolarSystem.Value.Moon.PositionKilometers - expectedEye);
        double altitude = Math.Asin(expected.Z / expected.Length) * 180 / Math.PI;
        Assert.InRange(Math.Abs(session[0].EarthObserver!.Value.ElevationDegrees - altitude), 0, 1e-10);
        Assert.Equal(other, session[1]);
        Assert.Throws<ArgumentException>(() => session.SetScriptAimTarget(0, new("missing/target")));
    }

    [Theory]
    [InlineData(-1)] [InlineData(301)] [InlineData(double.NaN)]
    public void InvalidScriptFlightDurationPreservesBothCameraAndFlight(double duration)
    {
        var session = SharedClockTests.Create(); session.OpenSpaceView(0);
        var camera = session[0].Navigation!.Value; var snapshot = session.World.Graph.Evaluate(session[0].Epoch);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.FlyToScriptPose(0, camera, duration, snapshot));
        Assert.Equal(camera, session[0].Navigation); Assert.False(session.AnyFlight);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, double.NaN)]
    public void InvalidScriptRotationCannotChangeTheObserver(double axis, double angle)
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.TurnScriptView(0, new(axis, 0, 0), angle, false,
            session.World.Graph.Evaluate(session[0].Epoch)));
        Assert.Equal(ScenarioFile.Encode(before), ScenarioFile.Encode(session.CaptureScenario(0)));
    }
}
