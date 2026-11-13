// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private (ObserverEventContext Context, MeteorPeak Peak)? _nextMeteorPeak;

    private void ToggleMeteors()
    {
        var observer = Session![_active];
        if (_brightSky is null || observer.Mode != CameraMode.Surface || observer.Navigation is not null)
        {
            _status.Text = "Meteor showers are visible from the Earth surface.";
            return;
        }
        bool shown = !observer.Sky.Meteors && !observer.Sky.ShowMeteorRadiants;
        Session.SetSkyLayers(_active, observer.Sky with { Meteors = shown, MeteorRadiants = null });
        Refresh();
        _status.Text = shown ? "Meteor radiants and trails visible · Ctrl+Shift+H for the next peak" : "Meteor layer hidden.";
    }

    private void FindMeteorPeak()
    {
        var context = CurrentEventContext();
        _nextMeteorPeak = null;
        if (_brightSky?.Meteors.NextPeak(context.World.Ephemeris, Session![_active].Epoch) is not { } found)
        {
            _status.Text = "No major shower peak within the covered calendar.";
            return;
        }
        _nextMeteorPeak = (context, found);
        _status.Text = $"{found.Shower.Name} peak · {CivilTimeLabel(found.Epoch, context.DeltaT)} · Ctrl+Alt+H to go";
    }

    private void GoToMeteorPeak()
    {
        if (_nextMeteorPeak is not { } next || next.Context != CurrentEventContext())
        {
            _status.Text = "Find the next major shower peak in this view using Ctrl+Shift+H.";
            return;
        }
        Session!.SetPlaying(_active, false);
        Seek(_active, next.Peak.Epoch);
        _status.Text = $"{next.Peak.Shower.Name} peak";
    }
}
