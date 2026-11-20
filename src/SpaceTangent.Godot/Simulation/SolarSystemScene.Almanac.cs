// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private sealed record AlmanacContext(SimulationWorld World, int View, EarthObserverCamera? Site, DeltaTModel DeltaT);
    private CancellationTokenSource? _almanacSearch;
    private VBoxContainer _almanacRows = null!;
    private Label _almanacSummary = null!;

    private void BuildAlmanacControls()
    {
        var page = _workspace.AddPage("Events", WorkspaceText.Choose("Nearby events", "Ближайшие события"), "Location");
        page.AddChild(SimulatorTheme.Detail(WorkspaceText.Choose(
            "45 days: geocentric phases, approaches, elongations and seasons; eclipses at this Earth site. The first global Jovian shadow is searched over 3 days. Meteor maxima are dates, not precise times.",
            "45 дней: геоцентрические фазы, сближения, элонгации и сезоны; затмения для земной площадки. Первая глобальная тень на Юпитере — за 3 дня. Максимумы метеоров указаны датой, без точного времени.")));
        AddButton(page, "Calculate nearby events", () => _ = FindNearbyEvents()).Name = "CalculateAlmanac";
        _almanacSummary = SimulatorTheme.Detail("Calculate from the active view's epoch and site.");
        _almanacSummary.Name = "AlmanacSummary";
        page.AddChild(_almanacSummary);
        _almanacRows = new() { Name = "AlmanacRows" };
        page.AddChild(_almanacRows);
    }

    private AlmanacContext CurrentAlmanacContext()
    {
        var session = Session!;
        return new(session.World, session.ViewIdentity(_active), session[_active].EarthObserver, session.DeltaT);
    }

    private async Task FindNearbyEvents()
    {
        var session = Session!;
        var observer = session[_active];
        var context = CurrentAlmanacContext();
        _almanacSearch?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _almanacSearch = cancellation;
        ClearAlmanacRows();
        _almanacSummary.Text = "Calculating nearby events…";
        var meteors = _brightSky?.Meteors;
        try
        {
            var result = await Task.Run(() => NearbyAlmanac.Find(context.World, observer, context.DeltaT, meteors,
                cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree() || !ReferenceEquals(session, Session) ||
                context != CurrentAlmanacContext() || session[_active].Epoch != observer.Epoch) return;
            _almanacSummary.Text = $"{result.Count} events · from {CivilTimeLabel(observer.Epoch, context.DeltaT)}";
            foreach (var entry in result)
            {
                string time = entry.DateOnly ? CivilCalendarDisplay.CivilDay(entry.Epoch, context.DeltaT)?.ToString() ?? "—" :
                    CivilTimeLabel(entry.Epoch, context.DeltaT);
                AddButton(_almanacRows, time + "\n" + entry.Description, () =>
                {
                    if (context != CurrentAlmanacContext())
                    {
                        _status.Text = "Recalculate events for the current view, site, data and ΔT.";
                        return;
                    }
                    session.SetPlaying(_active, false);
                    if (entry.Target is { } target && session.World.Catalogue.Find(target) is not null)
                        session.SelectObject(_active, target);
                    Seek(_active, entry.Epoch);
                    _status.Text = entry.Description;
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            if (!cancellation.IsCancellationRequested && IsInsideTree() && context == CurrentAlmanacContext())
                _almanacSummary.Text = "Could not calculate nearby events: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_almanacSearch, cancellation)) _almanacSearch = null;
        }
    }

    private void ClearAlmanacRows()
    {
        foreach (Node child in _almanacRows.GetChildren())
        {
            _almanacRows.RemoveChild(child);
            child.QueueFree();
        }
    }
}
