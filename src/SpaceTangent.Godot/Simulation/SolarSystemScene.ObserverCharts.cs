// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private readonly ObserverCalculationSlot _chartSearch = new();
    private (ObserverEventContext Context, ObserverCalculationResult Result)? _charts;
    private ObserverChart _dayChart = null!, _yearChart = null!, _moonChart = null!;
    private Label _chartSummary = null!, _yearSummary = null!;
    private VBoxContainer _chartTable = null!;

    private void BuildObserverCharts()
    {
        var page = _workspace.AddPage("Charts", WorkspaceText.Choose("Observing charts", "Графики наблюдений"), "Location");
        page.AddChild(SimulatorTheme.Detail("Calculate for the selected object, site, local civil clock and ΔT. Blue: target; gold: Sun; grey: Moon. Click a chart or table row to seek. Gaps mean unavailable data; scripted add-on callbacks are not run by this background calculation."));
        AddButton(page, "Calculate charts and 30 days", () => _ = CalculateObserverCharts()).Name = "CalculateCharts";
        _chartSummary = SimulatorTheme.Detail("Select an object before calculating.");
        _chartSummary.Name = "ChartSummary";
        page.AddChild(_chartSummary);
        Heading("Altitude · local civil day (°)");
        _dayChart = new() { Name = "DayAltitudeChart" };
        _dayChart.SampleChosen += index => { if (CurrentCharts() is { } result && result.Days[0].Day.Samples[index].AltitudeDegrees is not null) SeekChart(result.Days[0].Day.Samples[index].Epoch); };
        page.AddChild(_dayChart);
        Heading("Midnight altitude · civil year (°)");
        _yearSummary = SimulatorTheme.Detail("");
        page.AddChild(_yearSummary);
        _yearChart = new() { Name = "YearAltitudeChart" };
        _yearChart.SampleChosen += index => { if (CurrentCharts() is { } result && result.Year[index].Midnight.AltitudeDegrees is not null) SeekChart(result.Year[index].Midnight.Epoch); };
        page.AddChild(_yearChart);
        Heading("Angle to Moon · 30 days (°)");
        _moonChart = new() { Name = "MoonAngleChart" };
        _moonChart.SampleChosen += index => { if (CurrentCharts() is { } result && result.MoonAngles[index].MoonSeparationDegrees is not null) SeekChart(result.MoonAngles[index].Epoch); };
        page.AddChild(_moonChart);
        AddButton(page, "Export 30-day CSV", ExportObserverCharts).Name = "ExportCharts";
        page.AddChild(SimulatorTheme.Detail("Rise / highest altitude / set at a level horizon. Standard Earth refraction and Sun/Moon disc estimates; magnitudes use an approximate Lambert model. A dash is unavailable or has no crossing. Times below use UTC, or modeled UT1 before UTC coverage."));
        _chartTable = new() { Name = "ObserverChartTable" };
        page.AddChild(_chartTable);
        void Heading(string title)
        {
            var label = SimulatorTheme.Section(title);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            page.AddChild(label);
        }
    }

    private ObserverCalculationResult? CurrentCharts() => _charts is { } charts && charts.Context == CurrentEventContext() ? charts.Result : null;

    private async Task CalculateObserverCharts()
    {
        if (!CanStartSceneWork) return;
        var session = Session!;
        var observer = session[_active];
        var context = CurrentEventContext();
        _chartSearch.Cancel();
        using var cancellation = _chartSearch.Begin();
        using var resourceUse = _addonResources.Borrow(context.World.DistantStars);
        _charts = null;
        foreach (Node row in _chartTable.GetChildren()) { _chartTable.RemoveChild(row); row.QueueFree(); }
        _dayChart.SetData(-90, 90, []); _yearChart.SetData(-90, 90, []); _moonChart.SetData(0, 180, []);
        _yearSummary.Text = "";
        _chartSummary.Text = "Calculating observing charts…";
        try
        {
            var result = await Task.Run(() => ObserverCalculations.Calculate(context.World, observer, context.DeltaT,
                cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree()) return;
            if (!ReferenceEquals(session, Session) || CurrentEventContext() != context)
            { _chartSummary.Text = "Observer conditions changed. Recalculate charts."; return; }
            _charts = (context, result);
            _chartSummary.Text = $"{result.Name} · from {result.FirstDate} · {result.Days.Count} days";
            var samples = result.Days[0].Day.Samples;
            _dayChart.SetData(-90, 90, [samples.Select(row => row.AltitudeDegrees).ToArray(),
                samples.Select(row => row.SunAltitudeDegrees).ToArray(), samples.Select(row => row.MoonAltitudeDegrees).ToArray()]);
            _yearChart.SetData(-90, 90, [result.Year.Select(row => row.Midnight.AltitudeDegrees).ToArray()],
                result.Year.Select(row => row.Midnight.SunAltitudeDegrees).ToArray(), result.Year.Select(row => row.VisibleAtNight).ToArray());
            _yearSummary.Text = $"{result.Year.Count} civil dates · {result.Year.Count(row => row.VisibleAtNight is true)} nights above 10° while Sun below −12°\n" +
                (result.BestNight is { } best ? FormattableString.Invariant($"Best midnight: {best} · {result.BestNightAltitude:F1}°") : "No qualifying midnight sample.") +
                "\nBottom strip: green visible / grey not visible / muted red no data. Gold shade: midnight sunlight.";
            _moonChart.SetData(0, 180, [result.MoonAngles.Select(row => row.MoonSeparationDegrees).ToArray()]);
            foreach (var row in result.Days)
            {
                var maximum = row.Day.Maximum;
                string text = row.Date + " · " + Clock(row.Day.Rise) + " / " + Clock(maximum?.Epoch) + " / " + Clock(row.Day.Set) + "\n" +
                    FormattableString.Invariant($"Altitude {maximum?.AltitudeDegrees:F1}° · est. mag {maximum?.Magnitude:F1} · elongation {maximum?.ElongationDegrees:F1}°") +
                    (row.Day.CompleteCoverage ? "" : " · partial/no data");
                var button = AddButton(_chartTable, text, () => { if (maximum is { } at) SeekChart(at.Epoch); });
                button.Disabled = maximum is null;
                button.ClipText = true;
                button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                button.TooltipText = text;
            }
            string Clock(SpaceTangent.Contracts.JulianInstant? at) => at is { } epoch ? CivilTimeLabel(epoch, context.DeltaT) : "—";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            if (!cancellation.IsCancellationRequested && IsInsideTree() && CurrentEventContext() == context)
                _chartSummary.Text = "Could not calculate charts: " + error.Message;
        }
    }

    private void SeekChart(SpaceTangent.Contracts.JulianInstant epoch)
    {
        if (CurrentCharts() is null) { _status.Text = "Recalculate charts for the current observer and data."; return; }
        if (epoch.CompareTo(Session!.World.Ephemeris.Start) < 0 || epoch.CompareTo(Session.World.Ephemeris.End) > 0)
        { _status.Text = "No ephemeris at this chart sample."; return; }
        Session!.SetPlaying(_active, false);
        Seek(_active, epoch);
    }

    private void ExportObserverCharts()
    {
        if (CurrentCharts() is not { } result) { _status.Text = "Calculate charts in this view before exporting."; return; }
        string directory = System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT") ??
            ProjectSettings.GlobalizePath("user://observations");
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"ephemerides-{result.FirstDate}-{Guid.NewGuid():N}.csv");
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew))) ObserverCalculations.WriteCsv(writer, result);
            _status.Text = $"30-day CSV saved to {path}";
        }
        catch (Exception error) when (IsLoadError(error)) { _status.Text = "Could not write ephemerides: " + error.Message; }
    }
}
