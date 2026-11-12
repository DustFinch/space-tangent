// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseObserverNight(string directory)
    {
        const string outputVariable = "SPACETANGENT_OBSERVATIONS_OUTPUT";
        string? previous = System.Environment.GetEnvironmentVariable(outputVariable);
        string output = Path.Combine(directory, "night-ephemerides");
        System.Environment.SetEnvironmentVariable(outputVariable, output);
        string restore = Path.Combine(directory, "before-observer-night.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var evening = TimeConversion.FromUtc(new(new(2024, 1, 15), 16), TimeScale.Tdb);
            var left = new ObserverView(evening, CameraMode.Surface, new ObjectId("hip/32349"), false)
                { EarthObserver = EarthObserverCamera.Moscow };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb),
                CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "sirius-night.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The winter Sirius and independent Jason-1 scene must open.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            var status = Find<Label>("WorkspaceStatus");
            await KeyPress(Key.N, ctrl: true);
            await Until(() => status.Text.StartsWith("Night ephemerides ready", StringComparison.Ordinal),
                "Calculating the Moscow night");
            var details = Find<Label>("HorizontalCoordinates");
            Require(details.IsVisibleInTree() && details.Text.Contains("Culmination", StringComparison.Ordinal),
                "The existing ephemerides pane must show the refined culmination.");
            await KeyPress(Key.Tab);
            await KeyPress(Key.N, ctrl: true, shift: true);
            Require(Session[0] == left && Session[1] == right, "A night result must stay with the observer that requested it.");
            await KeyPress(Key.Tab);
            await KeyPress(Key.N, ctrl: true, alt: true);
            string csv = Directory.GetFiles(output, "*.csv").Single();
            string[] rows = File.ReadAllLines(csv);
            var best = rows.Skip(1).Select(line => line.Split(',').Select(value =>
                double.Parse(value, CultureInfo.InvariantCulture)).ToArray())
                .Where(row => row[1] <= -12).MaxBy(row => row[2])!;
            Require(rows.Length == 30 && best[2] is > 17 and < 18 && best[3] is > 160 and < 200,
                "The CSV must contain every sample and the expected visible Sirius altitude and azimuth.");
            await KeyPress(Key.N, ctrl: true, shift: true);
            Require(Math.Abs(Session[0].Epoch.ToJulianDate() - best[0]) < 1e-8 &&
                Session[0].EarthObserver == left.EarthObserver && Session[1] == right &&
                Session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "The dark-sample command must seek to the exported instant and preserve the other view and model.");

            var polar = left with
            {
                Epoch = TimeConversion.FromUtc(new(new(2024, 6, 21), 16), TimeScale.Tdb),
                EarthObserver = EarthObserverCamera.Tromso
            };
            ScenarioFile.Save(path, new([polar, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The polar summer scene must open.");
            await KeyPress(Key.N, ctrl: true);
            await Until(() => status.Text.StartsWith("No covered nautical darkness", StringComparison.Ordinal),
                "Calculating polar summer visibility");
            GD.Print("UI: calculated winter Sirius and polar summer ephemerides, wrote 29 CSV samples, and kept Jason-1 independent");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(outputVariable, previous);
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after the night test.");
        }
    }
}
