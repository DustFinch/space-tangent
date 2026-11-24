// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private sealed record TonightContext(SimulationWorld World, int View, ObjectId Body, EarthSite Site,
        ObserverSiteClock? Clock, GroundSkyConditions? Conditions, DeltaTModel Model, int Generation);
    private readonly ObserverCalculationSlot _tonightSearch = new();
    private Label _tonightSummary = null!;
    private VBoxContainer _tonightRows = null!;

    private TonightContext CurrentTonightContext()
    {
        var session = Session!;
        var camera = session[_active].ObserverCamera;
        return new(session.World, session.ViewIdentity(_active), camera.SurfaceBody, camera.Site, camera.CivilClock,
            camera.Conditions, session.DeltaT, _loadGeneration);
    }

    private void BuildTonightControls(VBoxContainer page)
    {
        page.AddChild(SimulatorTheme.Section("Visible tonight"));
        page.AddChild(SimulatorTheme.Detail("Planets and Moon, installed named stars to magnitude 1.5, and known Messier objects to magnitude 8. Ten-minute samples above 10°; nautical darkness, with shallower twilight when necessary. Click a row to seek and point the surface camera."));
        AddButton(page, "Calculate visible tonight", () => _ = CalculateTonight()).Name = "CalculateTonight";
        _tonightSummary = SimulatorTheme.Detail("Calculate for this observer's site and civil night.");
        _tonightSummary.Name = "TonightSummary";
        page.AddChild(_tonightSummary);
        _tonightRows = new() { Name = "TonightRows" };
        page.AddChild(_tonightRows);
    }

    private async Task CalculateTonight()
    {
        if (!CanStartSceneWork) return;
        var session = Session!;
        var observer = session[_active];
        var context = CurrentTonightContext();
        _tonightSearch.Cancel();
        using var cancellation = _tonightSearch.Begin();
        using var resourceUse = _addonResources.Borrow(context.World.DistantStars);
        foreach (Node row in _tonightRows.GetChildren()) { _tonightRows.RemoveChild(row); row.QueueFree(); }
        _tonightSummary.Text = "Calculating tonight's visibility…";
        try
        {
            var result = await Task.Run(() => VisibleTonight.Calculate(context.World, observer, context.Model, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree()) return;
            if (!ReferenceEquals(session, Session) || context != CurrentTonightContext())
            { _tonightSummary.Text = "Observer conditions changed. Recalculate tonight's visibility."; return; }
            _tonightSummary.Text = result.Visibility switch
            {
                NightVisibility.NoEphemeris => "No complete ephemeris for this civil observing day.",
                NightVisibility.NoDarkness => "The Sun does not set in this observing day.",
                NightVisibility.BelowAltitude => "No listed target rises above 10° in this covered night.",
                _ => $"Night of {result.Date} · {result.Targets.Count} objects\n" +
                    FormattableString.Invariant($"Sun below {result.SunLimitDegrees:F2}° · ") +
                    CivilTimeLabel(result.From!.Value, context.Model) + " → " + CivilTimeLabel(result.To!.Value, context.Model)
            };
            foreach (var target in result.Targets)
            {
                string text = target.Name + " · " + target.Kind + "\n" + CivilTimeLabel(target.Best, context.Model) +
                    FormattableString.Invariant($"\nAltitude {target.BestDirection.AltitudeDegrees:F1}° · est. mag {target.Magnitude:F1}");
                var button = AddButton(_tonightRows, text, () =>
                {
                    if (context != CurrentTonightContext()) { _status.Text = "Recalculate tonight's visibility for this observer and data."; return; }
                    session.SetPlaying(_active, false);
                    session.SelectObject(_active, target.Id);
                    if (session[_active] is { Mode: CameraMode.Surface, Navigation: null })
                    {
                        var camera = session[_active].ObserverCamera;
                        session.SetEarthObserver(_active, camera with
                        {
                            AzimuthDegrees = target.BestDirection.AzimuthDegrees ?? camera.AzimuthDegrees,
                            ElevationDegrees = target.BestDirection.AltitudeDegrees
                        });
                    }
                    Seek(_active, target.Best);
                    _status.Text = target.Name + " · best ten-minute night sample";
                });
                button.ClipText = true;
                button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                button.TooltipText = text + "\nAbove 10°: " + CivilTimeLabel(target.Above, context.Model) + " → " + CivilTimeLabel(target.Below, context.Model);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (IsLoadError(error) || error is ArithmeticException)
        {
            if (!cancellation.IsCancellationRequested && IsInsideTree() && context == CurrentTonightContext())
                _tonightSummary.Text = "Could not calculate tonight's visibility: " + error.Message;
        }
    }
}
