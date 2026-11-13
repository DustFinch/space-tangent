// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class BootstrapSmoke
{
    private async Task CheckNavigation(SolarSystemScene scene)
    {
        var session = scene.Session!;
        var moon = SolarSystemSample.MoonId;
        var jason = new ObjectId("norad/26997");
        if (session.ViewCount == 2)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Solar);
        session.SetPlaying(0, false);
        scene.Seek(0, new(JulianInstant.J2000Day, 0, TimeScale.Tdb));
        var query = (LineEdit)scene.FindChild("ObjectQuery", recursive: true, owned: false);
        var go = (Button)scene.FindChild("GoToObject", recursive: true, owned: false);
        var track = (Button)scene.FindChild("TrackObject", recursive: true, owned: false);
        Select("Moon");
        Require(!go.Disabled && !track.Disabled, "The Moon card must expose both camera actions.");
        go.EmitSignal(Button.SignalName.Pressed);
        Require(session.IsFlying(0), "Go to must start a flight from the current view.");
        var initial = session[0].Navigation;
        await Until(() => session[0].Navigation != initial, "The flight must move the camera while time is paused.");
        scene._UnhandledKeyInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        var stopped = session[0].Navigation;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(!session.IsFlying(0) && session[0].Navigation == stopped, "Esc must stop the camera at its current pose.");

        go.EmitSignal(Button.SignalName.Pressed);
        await Until(() => !session.IsFlying(0), "The flight to the Moon must finish.");
        Require(session[0].Navigation is { Target: { } moonTarget, Tracking: false } &&
                moonTarget == moon && scene.Renderer(0).NavigationCameraCurrent,
            "The flight must publish the Moon navigation camera.");
        track.EmitSignal(Button.SignalName.Pressed);
        Require(session[0].Navigation is { Tracking: true } && track.Text == "Stop tracking",
            "Tracking must enable the camera lock and update its action.");
        var moonBefore = session[0].Navigation!.Value.AnchorKilometers;
        scene.Advance(0, 3600);
        var movedMoon = session.World.Graph.Evaluate(session[0].Epoch);
        Require(movedMoon.TryState(moon, out var lunarState) &&
                session[0].Navigation!.Value.AnchorKilometers == lunarState.PositionKilometers &&
                session[0].Navigation!.Value.AnchorKilometers != moonBefore,
            "Tracking must recenter the Moon at the new epoch.");

        var viewport = (SubViewportContainer)scene.FindChild("ViewCanvas0", recursive: true, owned: false);
        var pose = session[0].Navigation!.Value;
        var initialPlacement = pose.ResolvePlacement(movedMoon, session.DeltaT);
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new(40, -20) });
        var orbited = session[0].Navigation!.Value;
        Require(Math.Abs(Math.IEEERemainder(orbited.AzimuthRadians - pose.AzimuthRadians, Math.Tau) + .12) < 1e-12 &&
                Math.Abs(orbited.ElevationRadians - pose.ElevationRadians - .06) < 1e-12,
            "A tracking orbit must use the archived angle per pixel.");
        var orbitPlacement = orbited.ResolvePlacement(movedMoon, session.DeltaT);
        var (sin, cos) = Math.SinCos(-.12);
        Vector3d expectedForward = initialPlacement.Forward * cos +
            Vector3d.Cross(initialPlacement.Up, initialPlacement.Forward) * sin;
        Vector3d pitchAxis = Vector3d.Cross(expectedForward, initialPlacement.Up);
        var (sinPitch, cosPitch) = Math.SinCos(.06);
        Vector3d expectedUp = initialPlacement.Up * cosPitch + Vector3d.Cross(pitchAxis, initialPlacement.Up) * sinPitch;
        Require((orbitPlacement.Up - expectedUp).Length < 1e-12 &&
                (orbitPlacement.OffsetKilometers + orbitPlacement.Forward * pose.RangeKilometers).Length < 1e-6,
            "Orbiting must rotate the eye and screen up together in camera axes.");
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp });
        var adjusted = session[0].Navigation!.Value;
        Require(adjusted.Tracking && adjusted.AzimuthRadians != pose.AzimuthRadians &&
                adjusted.ElevationRadians != pose.ElevationRadians && adjusted.RangeKilometers < pose.RangeKilometers,
            "Right drag and the wheel must orbit and zoom without dropping tracking.");

        scene.ToggleSplit();
        ObserverView lunar = session[1];
        using var autoOrbitKey = new InputEventKey { Pressed = true, Keycode = Key.O };
        scene._UnhandledKeyInput(autoOrbitKey);
        Require(session[0].AutoOrbit is { Enabled: true } && session[1] == lunar,
            "Automatic orbit must start only in the active view.");
        var orbitStart = session[0].Navigation!.Value;
        await Until(() => session[0].Navigation != orbitStart, "A paused clock must still let the camera orbit.");
        var orbitEnd = session[0].Navigation!.Value;
        Require(session[0].Epoch == lunar.Epoch && session[1] == lunar &&
                orbitStart.AnchorKilometers == orbitEnd.AnchorKilometers && orbitStart.RangeKilometers == orbitEnd.RangeKilometers,
            "Automatic orbit must preserve both clocks, its range, and the other view.");
        scene._UnhandledKeyInput(autoOrbitKey);
        Require(session[0].AutoOrbit is { Enabled: false }, "The same key must stop automatic orbit.");
        adjusted = orbitEnd;
        var beforeOffCenter = session.CaptureScenario(0);
        var anchor = adjusted.ResolvePlacement(movedMoon, session.DeltaT);
        Vector3d screenRight = Vector3d.Cross(anchor.Forward, anchor.Up);
        var offCenter = anchor with { OffsetKilometers = (anchor.OffsetKilometers + screenRight * (.2 * adjusted.RangeKilometers)) / Math.Sqrt(1.04) };
        session.RestoreScenario(beforeOffCenter with { Views = [session[0] with { Navigation = adjusted with { Placement = offCenter } }, lunar] });
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new(30, 21) });
        var afterOrbit = session[0].Navigation!.Value.Placement!.Value;
        Vector3d before = CameraCoordinates(offCenter);
        Vector3d after = CameraCoordinates(afterOrbit);
        Require((before - after).Length < 1e-12 && session[1] == lunar,
            "A tracked off-center target must retain its screen position during a combined orbit drag.");
        session.RestoreScenario(beforeOffCenter);

        static Vector3d CameraCoordinates(NavigationPlacement placement)
        {
            Vector3d direction = placement.OffsetKilometers * (-1 / placement.OffsetKilometers.Length);
            return new(Vector3d.Dot(direction, Vector3d.Cross(placement.Forward, placement.Up)),
                Vector3d.Dot(direction, placement.Up), Vector3d.Dot(direction, placement.Forward));
        }

        double priorField = session[0].EffectiveFieldOfView;
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp, CtrlPressed = true });
        Require(Math.Abs(session[0].EffectiveFieldOfView - priorField * .85) < 1e-12 &&
                session[0].Navigation == adjusted && session[1] == lunar,
            "Ctrl+wheel must narrow the field independently without moving either camera.");
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelDown, CtrlPressed = true });
        Require(Math.Abs(session[0].EffectiveFieldOfView - priorField) < 1e-12 && session[1] == lunar,
            "The opposite wheel direction must restore the field.");

        scene.SetMode(0, CameraMode.Surface);
        ObserverView surface = session[0];
        viewport.EmitSignal(Control.SignalName.GuiInput,
            new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp });
        Require(Math.Abs(session[0].EffectiveFieldOfView - surface.EffectiveFieldOfView * .85) < 1e-12 &&
                session[0].EarthObserver == surface.EarthObserver && session[1] == lunar,
            "The observatory wheel must narrow its field without changing the observing site or other view.");
        using var narrower = new InputEventKey { Pressed = true, PhysicalKeycode = Key.Comma };
        using var wider = new InputEventKey { Pressed = true, PhysicalKeycode = Key.Period };
        double groundField = session[0].EffectiveFieldOfView;
        scene._UnhandledKeyInput(narrower);
        Require(Math.Abs(session[0].EffectiveFieldOfView - groundField * .8) < 1e-12 && session[1] == lunar,
            "Comma must narrow the active view independently.");
        scene._UnhandledKeyInput(wider);
        Require(Math.Abs(session[0].EffectiveFieldOfView - groundField) < 1e-12, "Period must restore the field.");
        var beforeOptics = session.CaptureScenario(0);
        session.SetOptics(0, new(ObserverOpticsLibrary.Default, true));
        scene._UnhandledKeyInput(narrower);
        Require(session[0].ActiveOptics?.Eyepiece.FocalMillimeters == 15 && session[1] == lunar &&
                session[0].EffectiveFieldOfView == beforeOptics.Views[0].EffectiveFieldOfView,
            "Comma must select the next higher magnification while the eyepiece is active.");
        using var eyepieceWheel = new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp };
        viewport.EmitSignal(Control.SignalName.GuiInput, eyepieceWheel);
        Require(session[0].ActiveOptics?.Eyepiece.FocalMillimeters == 10, "Wheel up must step to a stronger eyepiece.");
        scene._UnhandledKeyInput(wider);
        Require(session[0].ActiveOptics?.Eyepiece.FocalMillimeters == 15, "Period must step to a weaker eyepiece.");
        session.RestoreScenario(beforeOptics);
        var ground = session[0].EarthObserver ?? EarthObserverCamera.Moscow;
        using var drag = new InputEventMouseMotion { ButtonMask = MouseButtonMask.Right, Relative = new(40, -20) };
        viewport.EmitSignal(Control.SignalName.GuiInput, drag);
        double groundScale = Math.Tan(session[0].EffectiveFieldOfView * Math.PI / 360) / Math.Tan(25 * Math.PI / 180);
        var turnedGround = session[0].EarthObserver!.Value;
        Require(turnedGround.Site == ground.Site && session[1] == lunar &&
                Math.Abs(turnedGround.AzimuthDegrees - ground.AzimuthDegrees - .12 * groundScale * 180 / Math.PI) < 1e-10 &&
                Math.Abs(turnedGround.ElevationDegrees - ground.ElevationDegrees - .06 * groundScale * 180 / Math.PI) < 1e-10,
            "A ground drag must turn the view by its optical field while keeping the site and other view.");

        scene.SetMode(0, CameraMode.Solar);
        session.StartFreeFlight(0);
        session.SetFieldOfView(0, 10);
        const double sensitivity = 2.5;
        var beforeSensitivity = session.CaptureScenario(0);
        session.RestoreScenario(beforeSensitivity with { Views = [session[0] with { MouseSensitivity = sensitivity }, lunar] });
        var free = session[0].Navigation!.Value;
        viewport.EmitSignal(Control.SignalName.GuiInput, drag);
        var turned = session[0].Navigation!.Value;
        double freeScale = Math.Tan(5 * Math.PI / 180) / Math.Tan(25 * Math.PI / 180);
        Require(turned.AnchorKilometers == free.AnchorKilometers && turned.RangeKilometers == free.RangeKilometers &&
                turned.Placement!.Value.Forward != free.Placement!.Value.Forward && session[1] == lunar &&
                Math.Abs(Math.IEEERemainder(turned.AzimuthRadians - free.AzimuthRadians, Math.Tau) + .12 * freeScale * sensitivity) < 1e-12,
            "A free drag must use its own field of view and sensitivity without translating the eye or the second view.");

        var beforeInertia = session.CaptureScenario(0);
        session.RestoreScenario(beforeInertia with { Views = [session[0] with { CameraInertia = true }, lunar] });
        using var press = new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right };
        using var release = new InputEventMouseButton { ButtonIndex = MouseButton.Right };
        using var coast = new InputEventMouseMotion
        {
            ButtonMask = MouseButtonMask.Right, Relative = new(20, 0), Velocity = new(200, 0)
        };
        viewport.EmitSignal(Control.SignalName.GuiInput, press);
        viewport.EmitSignal(Control.SignalName.GuiInput, coast);
        scene._Input(release); // A release outside the viewport never reaches its GUI signal.
        var released = session[0].Navigation!.Value;
        scene._Process(.05);
        var firstCoast = session[0].Navigation!.Value;
        scene._Process(.05);
        var secondCoast = session[0].Navigation!.Value;
        double firstTurn = released.AzimuthRadians - firstCoast.AzimuthRadians;
        double secondTurn = firstCoast.AzimuthRadians - secondCoast.AzimuthRadians;
        Require(firstTurn > 0 && Math.Abs(secondTurn / firstTurn - Math.Exp(-.05 / .3)) < 1e-5 &&
                released.AnchorKilometers == secondCoast.AnchorKilometers && session[1] == lunar,
            "A released camera drag must coast with the archived decay without moving either eye.");
        using var zoom = new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.WheelUp };
        viewport.EmitSignal(Control.SignalName.GuiInput, zoom);
        Require(session[0].Navigation!.Value.RangeKilometers == secondCoast.RangeKilometers,
            "An inertial wheel notch must start a push instead of jumping the range.");
        scene._Process(.05);
        double zoomed = session[0].Navigation!.Value.RangeKilometers;
        Require(Math.Abs(zoomed / secondCoast.RangeKilometers - Math.Pow(.85, 1 - Math.Exp(-.05 / .3))) < 1e-12 &&
                session[1] == lunar, "The wheel push must use the archived exponential range and leave the other view intact.");
        scene._Notification((int)Node.NotificationApplicationFocusOut);
        scene._Process(.05);
        Require(session[0].Navigation!.Value.RangeKilometers == zoomed, "Focus loss must clear the pending wheel push.");

        viewport.EmitSignal(Control.SignalName.GuiInput, press);
        viewport.EmitSignal(Control.SignalName.GuiInput, coast);
        viewport.EmitSignal(Control.SignalName.GuiInput, release);
        viewport.EmitSignal(Control.SignalName.GuiInput, zoom);
        scene._Process(.05);
        var atStop = session[0].Navigation!.Value;
        using var stop = new InputEventKey { Pressed = true, Keycode = Key.Escape };
        scene._UnhandledKeyInput(stop);
        scene._Process(.05);
        Require(session[0].Navigation == atStop && session[1] == lunar,
            "Esc must stop both inertial motions at their current pose without changing the second view.");
        session.RestoreScenario(beforeInertia);
        session.SetFieldOfView(0, priorField);
        JulianInstant beforeJason = session[0].Epoch;
        Select("26997");
        Require(session[0].Epoch == beforeJason && session[1] == lunar,
            "Searching for Jason-1 must preserve both clocks and the other camera.");
        go.EmitSignal(Button.SignalName.Pressed);
        await Until(() => !session.IsFlying(0), "The flight to Jason-1 must finish.");
        track.EmitSignal(Button.SignalName.Pressed);
        Require(session[0].Navigation is { Tracking: true, Target: { } jasonTarget } && jasonTarget == jason &&
                scene.Renderer(0).SatelliteMesh(jason).Visible && session[1] == lunar,
            "Jason-1 must track its original mesh while the other view keeps its Moon camera.");
        var jasonBefore = session[0].Navigation!.Value.AnchorKilometers;
        scene.Advance(0, 3600);
        Require(session[0].Navigation!.Value.AnchorKilometers != jasonBefore && session[1] == lunar,
            "An hour step must follow Jason-1 without moving the other observer.");
        scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
        var expected = session.CaptureScenario(0);

        string directory = Directory.CreateTempSubdirectory("SpaceTangent-navigation-").FullName;
        string path = Path.Combine(directory, "two-cameras.stscene");
        string oldPath = Path.Combine(directory, "version-one.stscene");
        try
        {
            scene.GetNode<FileDialog>("SaveScenario").EmitSignal(FileDialog.SignalName.FileSelected, path);
            var saved = ScenarioFile.Load(path);
            Require(saved.Views.SequenceEqual(expected.Views) && saved.DeltaT == expected.DeltaT &&
                    saved.ActiveView == expected.ActiveView && saved.Synchronized == expected.Synchronized,
                "The scenario must serialize both exact camera poses and time settings.");
            string scienceRoot = System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
            string movedRoot = Path.Combine(directory, "science");
            foreach (string original in Directory.GetFiles(scienceRoot, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(movedRoot, Path.GetRelativePath(scienceRoot, original));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(original, destination);
            }
            var previousWorld = session.World;
            try
            {
                System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", movedRoot);
                Require(await scene.ReloadAddOns() && !ReferenceEquals(previousWorld, session.World) &&
                        session[0] == expected.Views[0] && session[1] == expected.Views[1],
                    "Publishing a fresh world with the original add-ons must retain both tracking cameras.");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", scienceRoot);
            }
            Require(await scene.ReloadAddOns() && session[0] == expected.Views[0] && session[1] == expected.Views[1],
                "Restoring the original add-on location must retain both cameras.");
            scene.ToggleSplit();
            scene.SetMode(0, CameraMode.Solar);
            scene.GetNode<FileDialog>("OpenScenario").EmitSignal(FileDialog.SignalName.FileSelected, path);
            Require(session.ViewCount == 2 && session[0] == expected.Views[0] && session[1] == expected.Views[1] &&
                    session.DeltaT == expected.DeltaT && scene.Renderer(0).NavigationCameraCurrent &&
                    scene.Renderer(1).NavigationCameraCurrent,
                "Opening the saved scenario must restore both cameras, epochs and ΔT.");

            scene.Synchronize(true, 0);
            scene.Advance(0, 3600);
            Require(session.Synchronized && session[0].Epoch == session[1].Epoch &&
                    session[0].Navigation is { Tracking: true } left && left.Target == jason &&
                    session[1].Navigation is { Tracking: true } right && right.Target == moon &&
                    left.AnchorKilometers != right.AnchorKilometers,
                "Sharing the clock must keep both cameras independently tracking their own objects.");
            Require(scene.LoadScenario(path) && !session.Synchronized && session[0].Epoch != session[1].Epoch,
                "Reopening the independent scenario must restore its two different clocks.");

            File.WriteAllText(oldPath, """
                {"version":1,"synchronized":true,"clockOwner":0,"activeView":0,"deltaT":"morrisonStephenson2004",
                 "views":[{"dayTdb":2451545,"nanosecondsOfDay":0,"camera":"lunar","selection":null,"playing":false}]}
                """);
            Require(scene.LoadScenario(oldPath) && session.ViewCount == 1 &&
                    session[0].Mode == CameraMode.Lunar && session[0].Navigation is null && scene.Renderer(0).MoonVisible,
                "A version 1 scenario must still open with its original preset camera.");
            Require(scene.LoadScenario(path), "The saved camera scenario must still reopen.");
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ACCEPTANCE_SCENES") is { Length: > 0 } scenes)
            {
                File.Copy(path, Path.Combine(scenes, "two-cameras.stscene"), overwrite: true);
                File.Copy(oldPath, Path.Combine(scenes, "version-one.stscene"), overwrite: true);
                scene.ShowFullMoon(1);
                scene.Seek(0, session[0].Epoch);
                Require(scene.SaveScenario(Path.Combine(scenes, "jason-full-moon.stscene")), "The showcase scenario must save.");
                Require(scene.LoadScenario(path), "Capturing the showcase must leave the navigation scenario restored.");
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_NAVIGATION_CAPTURE") is { Length: > 0 } capture)
            {
                scene.ShowFullMoon(1);
                scene.Seek(0, session[0].Epoch);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using Image image = GetViewport().GetTexture().GetImage();
                Require(image.SavePng(capture) == Error.Ok, "Could not capture the tracked Jason-1 and full Moon.");
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(oldPath);
            Directory.Delete(directory, recursive: true);
        }
        GD.Print("SpaceTangent navigation, tracking, reload and scenario compatibility smoke passed");

        void Select(string text)
        {
            query.Text = text;
            query.EmitSignal(LineEdit.SignalName.TextChanged, text);
            query.EmitSignal(LineEdit.SignalName.TextSubmitted, text);
        }

        async Task Until(Func<bool> ready, string failure)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!ready())
            {
                Require(timer.Elapsed.TotalSeconds < 8, failure);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
    }
}
