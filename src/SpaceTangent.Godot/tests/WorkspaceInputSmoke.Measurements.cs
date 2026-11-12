// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseMeasurements(string directory)
    {
        var epoch = new JulianInstant(2460600, 0, TimeScale.Tdb);
        var solar = Session.World.Ephemeris.Sample(epoch);
        var camera = new NavigationCamera(solar.Earth.PositionKilometers, 24_000, .2, .2, SolarSystemSample.EarthId, true);
        var observer = new ObserverView(epoch, CameraMode.Solar, SolarSystemSample.EarthId, false)
            { Navigation = camera, FieldOfViewDegrees = 60 };
        string file = Path.Combine(directory, "measurement-camera.stscene");
        ScenarioFile.Save(file, new([observer], true, 0, 0, Session.DeltaT));
        Require(_scene.LoadScenario(file), "The angle test needs a perspective camera with a known lens.");
        var canvas = Find<SubViewportContainer>("ViewCanvas0");
        Tree.Root.GuiGetFocusOwner()?.ReleaseFocus();
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        await KeyPress(Key.A, ctrl: true);
        var overlay = _scene.Measurements(0);
        Require(overlay.AngleEnabled, "Ctrl+A must enable the angle tool in the active view.");
        Rect2 bounds = canvas.GetGlobalRect();
        Vector2 middle = bounds.GetCenter();
        Vector2 from = middle - new Vector2(0, .15f * bounds.Size.Y);
        Vector2 to = middle + new Vector2(0, .15f * bounds.Size.Y);
        using (var down = new InputEventMouseButton { Position = from, ButtonIndex = MouseButton.Left, Pressed = true })
            Tree.Root.PushInput(down, inLocalCoords: true);
        using (var drag = new InputEventMouseMotion { Position = to, Relative = to - from, ButtonMask = MouseButtonMask.Left })
            Tree.Root.PushInput(drag, inLocalCoords: true);
        using (var up = new InputEventMouseButton { Position = to, ButtonIndex = MouseButton.Left })
            Tree.Root.PushInput(up, inLocalCoords: true);
        double expected = 2 * Math.Atan(.30 * Math.Tan(Math.PI / 6)) * 180 / Math.PI;
        Require(overlay.AngleDegrees is { } measured && Math.Abs(measured - expected) < .25,
            $"The angle must come from projection rays (expected {expected:F3}°, observed {overlay.AngleDegrees:F3}°).");
        double angle = overlay.AngleDegrees!.Value;
        Session.AdjustCamera(0, .25, -.07, 1);
        _scene.Advance(0, 0);
        Require(overlay.AngleDegrees == angle && Session[0].Selection == SolarSystemSample.EarthId,
            "Angle endpoints must stay in the world as the camera turns, without changing the object selection.");
        await KeyPress(Key.A, ctrl: true);
        Require(!overlay.AngleEnabled && overlay.AngleDegrees is null, "Closing the angle tool must clear its rays.");

        await KeyPress(Key.M, ctrl: true);
        Session.SelectObject(0, SolarSystemSample.MoonId);
        await KeyPress(Key.M, ctrl: true);
        double distance = (solar.Moon - solar.Earth).PositionKilometers.Length;
        Require(overlay.DistanceKilometers is { } length && Math.Abs(length - distance) < 1e-7 &&
            overlay.DistanceText.Contains("light", StringComparison.Ordinal), "The distance and light time must use the real geometric state.");
        _scene.Advance(0, 3600);
        var later = Session.World.Ephemeris.Sample(Session[0].Epoch);
        Require(overlay.DistanceKilometers is { } next && Math.Abs(next - (later.Moon - later.Earth).PositionKilometers.Length) < 1e-7 &&
            Math.Abs(next - distance) > .1, "A retained distance pair must refresh as time changes.");
        await KeyPress(Key.M, ctrl: true, shift: true);
        Require(overlay.DistanceKilometers is null, "Clearing the measurement must remove the distance.");
        GD.Print("UI: measured a projected angle and the real changing Earth–Moon distance without system input");
    }
}
