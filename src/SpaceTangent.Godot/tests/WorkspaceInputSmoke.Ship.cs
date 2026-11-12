// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseShipFlight()
    {
        if (Session.ViewCount == 1)
            await KeyPress(Key.U, ctrl: true);
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        Session.Synchronize(false, 0);
        Session.SetPlaying(0, false);
        Session.OpenSpaceView(0);
        Session.Select(0, Jason);
        _scene.Seek(0, Session[0].Epoch);
        var epoch = Session[0].Epoch;
        var world = Session.World.Graph.Evaluate(epoch);
        Require(world.TryState(Jason, out var spacecraft), "The ship regression needs the original Jason-1 track.");
        var start = Session[0].Navigation!.Value;
        var initialEye = start.AnchorKilometers + start.ResolvePlacement(world, Session.DeltaT).OffsetKilometers;
        await KeyPress(Key.B, ctrl: true, shift: true);
        Require(Session.InShip(0) && !Session.InShip(1) && _scene.Renderer(0).ShipHullVisible &&
            (Session[0].Navigation!.Value.AnchorKilometers - initialEye).Length < 1e-6,
            "Boarding must keep the camera eye and show a hull in only the active view.");
        var aboard = Session.Ship(0)!;
        Session.SetShipThrottle(0, 100);
        Session.UpdateShip(0, world, .25);
        var moving = Session.Ship(0)!;
        Require((moving.Position - aboard.Position - aboard.Forward * 25).Length < 1e-6 && moving.Trail.Count > 1,
            "Manual flight must integrate wall time and record the trajectory.");
        await KeyPress(Key.X);
        var stopped = Session.Ship(0)!;
        Session.UpdateShip(0, world, .25);
        Require(Session.Ship(0)!.Position == stopped.Position && Session.Ship(0)!.SpeedKilometersPerSecond == 0,
            "The brake must stop motion at the current location.");
        await KeyPress(Key.C);
        Require(Session.Ship(0)!.Cockpit && !_scene.Renderer(0).ShipHullVisible && _scene.Renderer(0).ShipTrailVisible,
            "Cockpit mode must hide the hull and retain the trail.");

        await KeyPress(Key.G, ctrl: true, shift: true);
        double initial = (Session.Ship(0)!.Position - spacecraft.PositionKilometers).Length;
        for (int step = 0; step < 80; step++)
            Session.UpdateShip(0, world, .25);
        var arrival = Session.Ship(0)!;
        double standoff = (arrival.Position - spacecraft.PositionKilometers).Length;
        Require(arrival is { Pilot: ShipPilot.Approach, Target: { } target } && target == Jason &&
            standoff >= .999 && standoff < Math.Max(initial * 1e-5, 1.1),
            "Approach must converge on the real satellite and stop outside the hull.");
        await KeyPress(Key.O, ctrl: true, shift: true);
        Session.UpdateShip(0, world, .25);
        var orbit = Session.Ship(0)!;
        Require(orbit.Pilot == ShipPilot.Orbit && (orbit.Position - arrival.Position).Length > .005 &&
            Math.Abs((orbit.Position - spacecraft.PositionKilometers).Length - standoff) < .01,
            "Orbit must turn around the tracked target without changing its near-final radius.");
        var later = Session.World.Graph.Evaluate(epoch.AddSeconds(60));
        Require(later.TryState(Jason, out var moved), "The real trajectory must cover the next minute.");
        Session.UpdateShip(0, later, 0);
        Require((Session.Ship(0)!.Position - moved.PositionKilometers -
            (orbit.Position - spacecraft.PositionKilometers)).Length < 1e-6,
            "The autopilot must keep the same target-relative position across an epoch change.");
        Session.UpdateShip(0, world, 0);
        await KeyPress(Key.C);
        Require(_scene.Renderer(0).ShipHullVisible, "The external ship camera must restore the hull.");
        await KeyPress(Key.Escape);
        Require(!Session.InShip(0) && !_scene.Renderer(0).ShipHullVisible && !_scene.Renderer(0).ShipTrailVisible,
            "Leaving the ship must remove its hull and trail and return to an ordinary camera.");
        GD.Print("UI: boarded the shuttle, flew by hand, braked, switched cameras, approached and orbited real Jason-1, and left");
    }
}
