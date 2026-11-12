// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseObservationEvents(string directory)
    {
        string restore = Path.Combine(directory, "before-observation-events.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var date = new DateOnly(2024, 4, 8);
            var before = TimeConversion.FromUtc(new(date, 17), TimeScale.Tdb);
            var site = new EarthObserverCamera(new(25 + 17.4 / 60, -(104 + 7.7 / 60), 0), 149, 70);
            var left = new ObserverView(before, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = site };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "observation-events.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The surface eclipse and independent Jason-1 view must open.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            var status = Find<Label>("WorkspaceStatus");
            await KeyPress(Key.E, ctrl: true);
            await Until(() => status.Text.StartsWith("Total solar eclipse", StringComparison.Ordinal), "Finding the April 2024 eclipse");
            await KeyPress(Key.Tab);
            await KeyPress(Key.E, ctrl: true, shift: true);
            Require(Session[0].Epoch == before && Session[1].Epoch == right.Epoch,
                "The eclipse result must belong to the observer who searched for it.");
            await KeyPress(Key.Tab);
            await KeyPress(Key.E, ctrl: true, shift: true);
            var expected = TimeConversion.FromUtc(new(date, 18, 17, 15), TimeScale.Tdb);
            double seconds = (double)Session[0].Epoch.DifferenceNanoseconds(expected) / JulianInstant.NanosecondsPerSecond;
            Require(Math.Abs(seconds) < 100 && Session[0].EarthObserver == site &&
                Session[0].Selection == SolarSystemSample.SunId && Session[1] == right &&
                Session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "Seeking the eclipse must preserve the observer site, independent Jason-1 camera and ΔT.");
            GD.Print(FormattableString.Invariant($"UI: searched and opened the April 2024 eclipse with the other view unchanged; peak offset {seconds:F1} s from the published reference"));
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "The earlier workspace must reopen after the event test.");
        }
    }
}
