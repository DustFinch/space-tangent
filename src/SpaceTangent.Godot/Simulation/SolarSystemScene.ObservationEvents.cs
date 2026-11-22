// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private sealed record CalculationSite(ObjectId Body, EarthSite Site, ObserverSiteClock? Clock, GroundSkyConditions? Conditions);
    private static CalculationSite CalculationSiteOf(ObserverView observer)
    {
        var camera = observer.ObserverCamera;
        return new(camera.SurfaceBody, camera.Site, camera.CivilClock, camera.Conditions);
    }
    private sealed record ObserverEventContext(SimulationWorld World, int View, ObjectId? Selection,
        CalculationSite Site, DeltaTModel DeltaT, int Generation);

    private CancellationTokenSource? _eventSearch;
    private (ObserverEventContext Context, ObserverEvent Event)? _nextObserverEvent;

    private async Task FindNextObserverEvent()
    {
        var session = Session!;
        var observer = session[_active];
        var context = CurrentEventContext();
        _eventSearch?.Cancel();
        _nextObserverEvent = null;
        if (observer.EarthObserver is { IsEarth: false })
        {
            _status.Text = "Local events need an Earth site. Use nearby events or charts.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _eventSearch = cancellation;
        _status.Text = "Searching observation events…";
        try
        {
            var found = await Task.Run(() => ObserverEventFinder.Next(context.World, observer, context.DeltaT, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree() || !ReferenceEquals(session, Session) ||
                CurrentEventContext() != context)
                return;
            if (found is null)
            {
                _status.Text = "No covered observation event in the search interval.";
                return;
            }
            _nextObserverEvent = (context, found);
            _status.Text = $"{found.Description} · {CivilTimeLabel(found.Epoch, context.DeltaT)} · Ctrl+Shift+E to go";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            if (!cancellation.IsCancellationRequested && IsInsideTree())
                _status.Text = "Could not search observation events: " + error.Message;
        }
        finally
        {
            if (ReferenceEquals(_eventSearch, cancellation))
                _eventSearch = null;
        }
    }

    private void GoToObserverEvent()
    {
        if (_nextObserverEvent is not { } next || next.Context != CurrentEventContext())
        {
            _status.Text = "Search for an observation event in this view with Ctrl+E.";
            return;
        }
        var session = Session!;
        session.SetPlaying(_active, false);
        session.SelectObject(_active, next.Event.Target);
        if (session[_active] is { Mode: CameraMode.Surface, Navigation: null } &&
            next.Event.FlareDirection is { } direction)
        {
            var site = session[_active].ObserverCamera;
            session.SetEarthObserver(_active, site with
            {
                AzimuthDegrees = direction.AzimuthDegrees ?? site.AzimuthDegrees,
                ElevationDegrees = Math.Clamp(site.Conditions?.ApparentAltitude(direction.AltitudeDegrees, site.Site) ??
                    direction.AltitudeDegrees, -90, 90)
            });
        }
        Seek(_active, next.Event.Epoch);
        _status.Text = next.Event.Description;
    }

    private ObserverEventContext CurrentEventContext()
    {
        var session = Session!;
        var observer = session[_active];
        return new(session.World, session.ViewIdentity(_active), observer.Selection, CalculationSiteOf(observer), session.DeltaT, _loadGeneration);
    }
}
