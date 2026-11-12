// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private void ChangeGroundSky(bool toggle)
    {
        var observer = Session![_active];
        if (observer.Mode != CameraMode.Surface || observer.Navigation is not null)
        {
            _status.Text = "Ground sky conditions apply in the Earth surface view.";
            return;
        }
        var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
        GroundSkyConditions? conditions = camera.Conditions is { } current
            ? toggle ? null : current with { Bortle = current.Bortle % 9 + 1 }
            : new GroundSkyConditions(4);
        Session.SetEarthObserver(_active, camera with { Conditions = conditions });
        Refresh();
        _status.Text = conditions is { } chosen ? $"Ground sky · Bortle {chosen.Bortle}" : "Ground sky · geometric view";
    }
}
