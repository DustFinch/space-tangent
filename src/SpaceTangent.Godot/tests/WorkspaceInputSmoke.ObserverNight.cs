// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseObserverCharts(string directory)
    {
        string restore = Path.Combine(directory, "before-observer-charts.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        string? previous = System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT");
        string output = Path.Combine(directory, "chart-csv");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT", output);
        try
        {
            var left = new ObserverView(TimeConversion.FromUtc(new(new(2024, 1, 15), 16), TimeScale.Tdb),
                CameraMode.Surface, new ObjectId("hip/32349"), false) { EarthObserver = EarthObserverCamera.Moscow };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "charts.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The Sirius chart scene must open.");
            await Click(Find<Button>("ChartsTab"));
            await Click(Find<Button>("CalculateCharts"));
            var table = Find<VBoxContainer>("ObserverChartTable");
            await Until(() => table.GetChildCount() == 30, "Calculating annual and monthly observer charts", 60);
            Require(Find<Label>("ChartSummary").Text.Contains("Sirius", StringComparison.Ordinal), "Charts must retain their captured target.");
            // Use viewport input, without native mouse injection.
            var chart = Find<Control>("YearAltitudeChart");
            await Click(chart);
            Require(Session[0].Epoch != left.Epoch && Session[0].EarthObserver == left.EarthObserver &&
                Session[1] == right, "Clicking the annual chart must preserve both cameras and the independent epoch.");
            var export = Find<Button>("ExportCharts");
            await Click(export);
            string csv = Directory.GetFiles(output, "*.csv").Single();
            Require(File.ReadAllLines(csv).Length == 31, "The chart export must contain 30 daily rows.");
            await KeyPress(Key.Tab);
            export.EmitSignal(Button.SignalName.Pressed);
            Require(Directory.GetFiles(output, "*.csv").Length == 1, "A chart export must reject another observer.");
            await KeyPress(Key.Tab);
            Require(await _scene.ReloadAddOns(null), "Reloading the chart data.");
            export.EmitSignal(Button.SignalName.Pressed);
            Require(Directory.GetFiles(output, "*.csv").Length == 1 && Session[1] == right &&
                Session.DeltaT == DeltaTModel.MorrisonStephenson2004, "Charts must reject results from a replaced data generation.");
            GD.Print("UI: calculated daily/yearly Sirius altitude and monthly lunar angles, clicked the year chart, exported 30 days and rejected stale results.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT", previous);
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after charts.");
        }
    }

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
