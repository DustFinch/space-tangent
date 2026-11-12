// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseOptics(string directory)
    {
        const string variable = "SPACETANGENT_OPTICS_FILE";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        string local = Path.Combine(directory, "оптика 1.json");
        File.WriteAllText(local, """
            {
              "Telescopes": [
                { "Name": "My reflector", "Kind": "Dobson", "ApertureMillimeters": 150, "FocalMillimeters": 750 },
                { "Name": "Invalid", "ApertureMillimeters": 0, "FocalMillimeters": 100 }
              ],
              "Eyepieces": [
                { "Name": "My wide field", "FocalMillimeters": 20, "ApparentFieldDegrees": 60 }
              ]
            }
            """);
        System.Environment.SetEnvironmentVariable(variable, local);
        string restore = Path.Combine(directory, "before-optics.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var left = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
                { EarthObserver = EarthObserverCamera.Moscow, FieldOfViewDegrees = 12 };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "observer-optics.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The Moon and independent Jason-1 optics fixture must open.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            await KeyPress(Key.Key0, ctrl: true);
            Require(Session[0].ActiveOptics?.Magnification() == 48 && _scene.Renderer(0).EyepieceVisible &&
                !_scene.Renderer(1).EyepieceVisible, "The archived 48x configuration should affect only the active view.");
            for (int i = 0; i < 3; i++)
                await KeyPress(Key.Key0, ctrl: true, shift: true);
            for (int i = 0; i < 4; i++)
                await KeyPress(Key.Key0, ctrl: true, alt: true);
            await KeyPress(Key.Key0, shift: true);
            await KeyPress(Key.Key0, alt: true);
            var profile = Session[0].ActiveOptics;
            Require(profile is { Inverted: false, Barlow: 2 } && profile.Tube.Name == "My reflector" &&
                profile.Eyepiece.Name == "My wide field" && profile.TrueFieldDegrees() == .8,
                "The legacy-format local profiles, Barlow and flip commands must be applied.");
            var scenario = Session.CaptureScenario(0);
            ScenarioFile.Save(path, scenario);
            RequireScenario(scenario, ScenarioFile.Load(path), "Saving the active optics profile");
            await KeyPress(Key.Key0, ctrl: true);
            Require(Session[0].ActiveOptics is null && Session[0].Optics?.Profile == profile &&
                !_scene.Renderer(0).EyepieceVisible && Session[0].EffectiveFieldOfView == 12 &&
                Session[0].EarthObserver == left.EarthObserver && Session[1] == right,
                "Leaving the eyepiece must restore the base field and both original cameras.");
            GD.Print("UI: used built-in and archived-format optics, Barlow, inversion and restored the normal field with Jason-1 independent");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
            Require(_scene.LoadScenario(restore), "The earlier workspace must reopen after the optics test.");
        }
    }
}
