// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseMeteors(string directory)
    {
        string restore = Path.Combine(directory, "before-meteors.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var start = TimeConversion.FromUtc(new(new(2026, 8, 9), 0), TimeScale.Tdb);
            var left = new ObserverView(start, CameraMode.Surface, null, false) { EarthObserver = EarthObserverCamera.Moscow };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "meteor-calendar.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The meteor calendar fixture must open.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            var status = Find<Label>("WorkspaceStatus");
            await KeyPress(Key.H, ctrl: true);
            Require(Session[0].Sky.Meteors && !Session[1].Sky.Meteors, "Only the active surface view should show meteors.");
            await KeyPress(Key.H, ctrl: true, shift: true);
            Require(status.Text.StartsWith("Perseids peak", StringComparison.Ordinal), "The next peak must be the Perseids.");
            await KeyPress(Key.Tab);
            await KeyPress(Key.H, ctrl: true, alt: true);
            Require(Session[0].Epoch == start && Session[1] == right, "The other view must not take the meteor result.");
            await KeyPress(Key.Tab);
            await KeyPress(Key.H, ctrl: true, alt: true);
            var date = TimeConversion.ToUtc(Session[0].Epoch).Date;
            Require(date >= new DateOnly(2026, 8, 12) && date <= new DateOnly(2026, 8, 13) &&
                Session[0].Sky.Meteors && Session[1] == right && Session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "Seeking the meteor maximum must preserve the other view and the selected Earth model.");
            var scenario = Session.CaptureScenario(0);
            ScenarioFile.Save(path, scenario);
            RequireScenario(scenario, ScenarioFile.Load(path), "Saving independent meteor layers");
            GD.Print("UI: enabled meteor radiants, sought the Perseid peak and preserved the independent Jason-1 view");
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after the meteor calendar test.");
        }
    }
}
