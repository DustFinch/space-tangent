// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseClockTools(string directory)
    {
        string restore = Path.Combine(directory, "before-clock-tools.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var epoch = JulianInstant.FromJulianDate(2460600.5, TimeScale.Tdb);
            var sample = Session.World.Ephemeris.Sample(epoch);
            var moon = SolarSystemSample.MoonId;
            var camera = new NavigationCamera(sample.Earth.PositionKilometers, 24_000, 0, 0, null, false);
            var left = new ObserverView(epoch, CameraMode.Solar, moon, false) { Navigation = camera, PlaybackRate = 600 };
            var right = left with { Epoch = epoch.AddSeconds(3600), PlaybackRate = 1 };
            string path = Path.Combine(directory, "clock-tools.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The independent clock fixture must open.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();

            await KeyPress(Key.Slash, '?', shift: true);
            Require(Find<Label>("WorkspaceStatus").Text.StartsWith("Light travel time", StringComparison.Ordinal),
                "The question-mark key must report the selected body's light time.");
            double seconds = (sample.Moon.PositionKilometers - sample.Earth.PositionKilometers + new Vector3d(0, 24_000, 0)).Length /
                SimulationSession.LightKilometersPerSecond;
            await KeyPress(Key.Minus, '-');
            Require(Session[0].LightTimeDelayed && Session[0].Epoch == epoch.AddSeconds(-seconds) &&
                !Session[1].LightTimeDelayed && Session[1].Epoch == right.Epoch,
                "The light-time shortcut must move only the active independent clock.");

            var delayed = Session.CaptureScenario(0);
            ScenarioFile.Save(path, delayed);
            RequireScenario(delayed, ScenarioFile.Load(path), "Saving the light-time state");
            var addOns = Find<Button>("AddOnsTab");
            if (!addOns.ButtonPressed)
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Reloading both views with light-time correction");
            RequireScenario(delayed, Session.CaptureScenario(0), "Reloading with the light-time state");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            double delayAtShiftedEpoch = Session.LightTimeSeconds(0)!.Value;
            await KeyPress(Key.Minus, '-');
            Require(!Session[0].LightTimeDelayed && Session[0].Epoch == delayed.Views[0].Epoch.AddSeconds(delayAtShiftedEpoch),
                "Turning off light time must measure the forward shift at the current epoch.");

            await KeyPress(Key.K);
            Require(Session[0].PlaybackRate == 60 && !Session[0].Playing, "K must slow a paused clock without starting it.");
            await KeyPress(Key.L);
            Require(Session[0].PlaybackRate == 600 && !Session[0].Playing, "L must restore the playback rate.");
            var beforeReverse = Session[0].Epoch;
            await KeyPress(Key.J);
            await Until(() => Session[0].Epoch.CompareTo(beforeReverse) < 0, "Reversing the first observer clock");
            Require(Session[0].PlaybackRate == -600 && Session[0].Playing && Session[1].Epoch == right.Epoch,
                "J must start reverse playback only in the active independent view.");
            await KeyPress(Key.Space);
            Require(!Session[0].Playing, "Space must pause the reversed clock.");

            await Click(Find<Button>("TimeTab"));
            await FillField("UtcDate", "2016-12-31");
            await FillField("UtcTime", "23:59:60");
            await KeyPress(Key.Enter);
            Require(TimeConversion.ToUtc(Session[0].Epoch) is { Second: 60 } &&
                Session[1].Epoch == right.Epoch, "The time input must retain the published leap second and the other epoch.");
            GD.Print("UI: measured and toggled light time, preserved it through reload, and checked reverse speed and the 2016 UTC leap second");
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "The earlier workspace must reopen after the clock test.");
        }
    }
}
