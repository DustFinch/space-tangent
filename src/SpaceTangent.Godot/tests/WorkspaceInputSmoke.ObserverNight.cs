// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseVisibleTonight(string directory)
    {
        string restore = Path.Combine(directory, "before-visible-tonight.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var left = new ObserverView(TimeConversion.FromUtc(new(new(2024, 1, 15), 16), TimeScale.Tdb),
                CameraMode.Surface, null, false) { EarthObserver = EarthObserverCamera.Moscow };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "visible-tonight.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The visible-tonight scene must open.");
            await Click(Find<Button>("EphemeridesTab"));
            Session.SetPlaying(0, true);
            await Click(Find<Button>("CalculateTonight"));
            var rows = Find<VBoxContainer>("TonightRows");
            await Until(() => rows.GetChildCount() > 0, "Calculating visible objects while the clock runs", 30);
            var sirius = rows.GetChildren().OfType<Button>().Single(button => button.Text.StartsWith("Sirius ·", StringComparison.Ordinal));
            await Click(sirius);
            Require(Session[0].Selection == new ObjectId("hip/32349") && !Session[0].Playing &&
                Session[0].EarthObserver is { ElevationDegrees: > 17 and < 18 } && Session[1] == right,
                "A night row must seek and point Sirius without moving the independent Jason-1 observer.");
            var another = rows.GetChildren().OfType<Button>().First(button => button != sirius);
            another.EmitSignal(Button.SignalName.Pressed);
            Require(!Find<Label>("WorkspaceStatus").Text.StartsWith("Recalculate", StringComparison.Ordinal),
                "Selecting another row must remain valid after the first row repoints the same site.");
            var held = Session[0];
            await KeyPress(Key.Tab);
            sirius.EmitSignal(Button.SignalName.Pressed);
            Require(Session[0] == held && Session[1] == right, "A night list must belong to the view that requested it.");
            await KeyPress(Key.Tab);
            Require(await _scene.ReloadAddOns(null), "Reloading the visible-tonight data.");
            sirius.EmitSignal(Button.SignalName.Pressed);
            Require(Session[0] == held && Session[1] == right && Session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "A night list must reject a prior source generation.");
            GD.Print("UI: calculated visible tonight while playing, pointed Sirius and another listed object, retained Jason-1 and rejected other-view/reloaded rows.");
        }
        finally { Require(_scene.LoadScenario(restore), "The previous workspace must reopen after the night list."); }
    }

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
            await TurnSurfaceWithViewportInput();
            var turned = Session[0].EarthObserver;
            Require(turned != left.EarthObserver, "The viewport drag must turn the surface camera.");
            // Use viewport input, without native mouse injection.
            var chart = Find<Control>("YearAltitudeChart");
            await Click(chart);
            Require(Session[0].Epoch != left.Epoch && Session[0].EarthObserver == turned &&
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
            var edge = left with { Epoch = Session.World.Ephemeris.End.AddSeconds(-3600) };
            ScenarioFile.Save(path, new([edge, right], false, 0, 0, Session.DeltaT));
            Require(_scene.LoadScenario(path), "The coverage-edge chart scene must open.");
            await Click(Find<Button>("ChartsTab"));
            await Click(Find<Button>("CalculateCharts"));
            await Until(() => table.GetChildCount() == 30, "Calculating charts at the end of coverage", 60);
            Session.SetPlaybackRate(0, 1);
            Session.SetPlaying(0, true);
            await Click(Find<Control>("MoonAngleChart"));
            Require(Session[0].Playing && Math.Abs((double)Session[0].Epoch.DifferenceNanoseconds(edge.Epoch) /
                JulianInstant.NanosecondsPerSecond) < 10 && Session[1] == right,
                "An unavailable chart sample must neither seek nor pause the clock or affect the other view.");
            GD.Print("UI: turned the camera after calculating, clicked the year chart, exported 30 days, rejected stale results and ignored a sample beyond coverage without pausing.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT", previous);
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after charts.");
        }
    }

    private async Task TurnSurfaceWithViewportInput()
    {
        var canvas = Find<SubViewportContainer>("ViewCanvas0");
        Vector2 point = canvas.GetGlobalRect().GetCenter();
        using (var enter = new InputEventMouseMotion { Position = point, GlobalPosition = point })
            Tree.Root.PushInput(enter, inLocalCoords: true);
        using (var down = MouseButtonAt(point, true, MouseButton.Right)) Tree.Root.PushInput(down, inLocalCoords: true);
        using (var motion = new InputEventMouseMotion { Position = point + new Vector2(35, 12),
            GlobalPosition = point + new Vector2(35, 12), Relative = new(35, 12), ButtonMask = MouseButtonMask.Right })
            Tree.Root.PushInput(motion, inLocalCoords: true);
        using (var up = MouseButtonAt(point + new Vector2(35, 12), false, MouseButton.Right)) Tree.Root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree.CreateTimer(1), SceneTreeTimer.SignalName.Timeout);
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
