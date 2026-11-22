// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NavigationOperationBoundaryTests
{
    [Fact]
    public void MissingStatesAndFramesCannotMoveTheCurrentCamera()
    {
        var session=SharedClockTests.Create(); session.StartFreeFlight(0); session.SelectObject(0,new("sol/ceres"));
        byte[] before=ScenarioFile.Encode(session.CaptureScenario(0));
        var snapshot=session.World.Graph.Evaluate(session[0].Epoch);
        Assert.False(session.FocusSelection(0)); Assert.False(session.FollowBodyRotation(0));
        Assert.False(session.FollowScriptReference(0,new("missing/target"),NavigationFrame.Inertial));
        Assert.False(session.FollowRelativeFrame(0,NavigationFrame.PhaseLock,SolarSystemSample.MoonId));
        Assert.False(session.DollyScriptView(0,1,snapshot));
        session.TurnScriptView(0,new(0,1,0),.1,true,snapshot);
        Assert.Equal(before,ScenarioFile.Encode(session.CaptureScenario(0)));
        session.BoardShip(0); Assert.False(session.SetShipPilot(0,ShipPilot.Approach));
    }

    [Fact]
    public void LosingTheDestinationStateCancelsAScriptFlightWithoutInventingAPosition()
    {
        var session=SharedClockTests.Create(); session.StartFreeFlight(0);
        var snapshot=session.World.Graph.Evaluate(session[0].Epoch);
        var destination=session[0].Navigation!.Value with {Target=new("sol/ceres"),Tracking=true};
        Assert.True(session.FlyToScriptPose(0,destination,2,snapshot));
        var before=session[0].Navigation; Assert.True(session.IsFlying(0));
        session.UpdateCamera(0,snapshot,.1);
        Assert.False(session.IsFlying(0)); Assert.Equal(before,session[0].Navigation);
    }

    [Fact]
    public void TurningOffBodyTrackingPreservesTheCurrentInertialPose()
    {
        var session=SharedClockTests.Create(); session.OpenSpaceView(0);
        session.SelectObject(0,SolarSystemSample.EarthId);
        Assert.True(session.FollowBodyRotation(0));
        var snapshot=session.World.Graph.Evaluate(session[0].Epoch);
        var pose=session[0].Navigation!.Value.ResolvePlacement(snapshot,session.DeltaT);
        Assert.True(session.ToggleTracking(0)); var stopped=session[0].Navigation!.Value;
        Assert.False(stopped.Tracking); Assert.False(stopped.Placement!.Value.UsesFrame);
        Assert.InRange((pose.Forward-stopped.Placement.Value.Forward).Length,0,1e-12);
        Assert.InRange((pose.OffsetKilometers-stopped.Placement.Value.OffsetKilometers).Length,0,1e-8);
    }

    [Fact]
    public void OrbitAndDollyAtThePivotCannotCreateADegenerateCamera()
    {
        var session=SharedClockTests.Create(); session.StartFreeFlight(0); session.SelectObject(0,SolarSystemSample.EarthId);
        var snapshot=session.World.Graph.Evaluate(session[0].Epoch);
        Assert.True(snapshot.TryState(SolarSystemSample.EarthId,out var earth));
        var pose=new NavigationCamera(earth.PositionKilometers,1,0,0,null,false)
            {Placement=new(default,new(0,0,-1),new(0,1,0))};
        Assert.True(session.FlyToScriptPose(0,pose,0,snapshot));
        Assert.False(session.DollyScriptView(0,1,snapshot));
        session.TurnScriptView(0,new(0,1,0),.1,true,snapshot);
        Assert.False(session.FocusSelection(0)); Assert.Equal(pose,session[0].Navigation);
    }

    [Theory]
    [InlineData(double.NaN,0,1)] [InlineData(0,double.PositiveInfinity,1)] [InlineData(0,0,0)]
    public void InvalidManualCameraAdjustmentsLeaveTheViewUnchanged(double yaw,double pitch,double zoom)
    {
        var session=SharedClockTests.Create(); session.OpenSpaceView(0); var before=session[0];
        Assert.False(session.AdjustCamera(0,yaw,pitch,zoom)); Assert.Equal(before,session[0]);
    }
}
