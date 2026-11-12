// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseReferenceMarks(string directory)
    {
        var earth = SolarSystemSample.EarthId;
        var epoch = new JulianInstant(2460600, 0, TimeScale.Tdb);
        var solar = Session.World.Ephemeris.Sample(epoch);
        var camera = new NavigationCamera(solar.Earth.PositionKilometers, 24_000, .3, .4, earth, true);
        var observer = new ObserverView(epoch, CameraMode.Solar, earth, false) { Navigation = camera };
        string path = Path.Combine(directory, "body-marks.stscene");
        ScenarioFile.Save(path, new([observer, observer], false, 0, 0, Session.DeltaT));
        Require(_scene.LoadScenario(path), "The marks test needs two perspective observers.");
        Tree.Root.GuiGetFocusOwner()?.ReleaseFocus();
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        await KeyPress(Key.B, ctrl: true);
        Require(Session[0].ReferenceMarks == BodyReferenceMarks.All && _scene.Renderer(0).ReferenceMarksVisible &&
            Session[1].ReferenceMarks == BodyReferenceMarks.None && !_scene.Renderer(1).ReferenceMarksVisible,
            "Ctrl+B must toggle the marks in the active view alone.");

        var snapshot = Session.World.Graph.Evaluate(epoch);
        BodyReferenceGeometry geometry = Session.ReferenceGeometry(0, snapshot) ??
            throw new InvalidOperationException("Earth geometry is missing.");
        Require(geometry.Body is not null && geometry.ToSun is not null && geometry.RelativeVelocity is not null,
            "Earth marks must have a body basis, sunlight and heliocentric velocity.");
        Basis3d body = geometry.Body!.Value;
        Vector3d pole = geometry.SurfacePoint(0, Math.PI / 2);
        Require((pole - body.Z * geometry.PolarRadius).Length < 1e-8 &&
            (geometry.SurfacePoint(Math.PI / 2, 0) - body.Y * geometry.EquatorialRadius).Length < 1e-8,
            "Planetocentric coordinates must use the physical pole and east longitude.");
        Vector3d sunlight = body.Transpose().Transform(geometry.ToSun!.Value);
        Vector3d velocity = (solar.Earth - solar.Sun).VelocityKilometersPerSecond;
        Require(Vector3d.Dot(geometry.RelativeVelocity!.Value, velocity / velocity.Length) > .999999999,
            "The velocity marker must use the motion relative to the parent.");
        for (int index = 0; index < 12; index++)
        {
            Vector3d point = body.Transpose().Transform(geometry.TerminatorPoint(index * Math.Tau / 12));
            double a = geometry.EquatorialRadius, b = geometry.PolarRadius;
            Vector3d normal = new(point.X / (a * a), point.Y / (a * a), point.Z / (b * b));
            Require(Math.Abs(Vector3d.Dot(normal / normal.Length, sunlight)) < 1e-10,
                "The local surface normal on the terminator must be perpendicular to sunlight.");
        }

        var accepted = Session.CaptureScenario(0);
        Require(await _scene.ReloadAddOns(), "The original add-ons must reload with reference marks enabled.");
        RequireScenario(accepted, Session.CaptureScenario(0), "Reloading an independently marked observer");
        ScenarioFile.Save(path, accepted);
        RequireScenario(accepted, ScenarioFile.Load(path), "Reading saved reference marks");
        Require(_scene.LoadScenario(path) && _scene.Renderer(0).ReferenceMarksVisible && !_scene.Renderer(1).ReferenceMarksVisible,
            "The saved view must restore its own reference marks.");
        Tree.Root.GuiGetFocusOwner()?.ReleaseFocus();
        await KeyPress(Key.B, ctrl: true);
        Require(Session[0].ReferenceMarks == BodyReferenceMarks.None && !_scene.Renderer(0).ReferenceMarksVisible,
            "Toggling the marks off must remove their geometry.");
        GD.Print("UI: rendered independent body marks, checked the real Earth terminator and relative velocity, and restored marks after reload");
    }
}
