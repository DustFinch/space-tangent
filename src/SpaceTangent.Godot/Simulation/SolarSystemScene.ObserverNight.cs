// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private (ObserverEventContext Context, ObserverNight Night)? _observerNight;

    private async Task FindObserverNight()
    {
        var session = Session!;
        var observer = session[_active];
        var context = CurrentEventContext();
        if (context.Selection is null)
        {
            _status.Text = "Select a target before calculating night ephemerides.";
            return;
        }
        _eventSearch?.Cancel();
        _observerNight = null;
        using var cancellation = new CancellationTokenSource();
        _eventSearch = cancellation;
        _status.Text = "Calculating night ephemerides…";
        try
        {
            var night = await Task.Run(() => ObserverNightFinder.ForSelection(context.World, observer,
                context.DeltaT, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree() || !ReferenceEquals(session, Session) ||
                CurrentEventContext() != context)
                return;
            _observerNight = (context, night);
            _workspace.Open("Ephemerides");
            UpdateActiveFrame();
            _status.Text = night.Visibility switch
            {
                NightVisibility.Visible => "Night ephemerides ready · Ctrl+Shift+N to go · Ctrl+Alt+N for CSV",
                NightVisibility.NoDarkness => "No covered nautical darkness · Ctrl+Alt+N for CSV",
                NightVisibility.BelowAltitude => "Target stays below 10° during darkness · Ctrl+Alt+N for CSV",
                _ => "Target has no covered night ephemeris · Ctrl+Alt+N for CSV"
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            if (!cancellation.IsCancellationRequested && IsInsideTree() && CurrentEventContext() == context)
                _status.Text = "Could not calculate night ephemerides. " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_eventSearch, cancellation))
                _eventSearch = null;
        }
    }

    private ObserverNight? CurrentObserverNight() =>
        _observerNight is { } entry && entry.Context == CurrentEventContext() ? entry.Night : null;

    private void GoToDarkSample()
    {
        if (CurrentObserverNight()?.BestDarkSample is not { } sample)
        {
            _status.Text = "Calculate a night with a visible dark sample in this view using Ctrl+N.";
            return;
        }
        Session!.SetPlaying(_active, false);
        Seek(_active, sample.Epoch);
        _status.Text = "Best dark sample · " + CivilTimeLabel(sample.Epoch, Session.DeltaT);
    }

    private void ExportObserverNight()
    {
        if (CurrentObserverNight() is not { } night)
        {
            _status.Text = "Calculate night ephemerides in this view with Ctrl+N.";
            return;
        }
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVATIONS_OUTPUT");
        string directory = string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://observations") : configured;
        string name = FormattableString.Invariant($"night-{night.EveningDate:yyyy-MM-dd}-{Guid.NewGuid():N}.csv");
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew)))
                ObserverEphemerides.WriteCsv(writer, night);
            _status.Text = $"Night CSV saved to {path}";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = "Could not write night ephemerides. " + error.Message;
        }
    }
}
