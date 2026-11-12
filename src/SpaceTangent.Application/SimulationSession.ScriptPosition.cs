// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    internal Vector3d ScriptObserverEye(int view, WorldSnapshot snapshot)
    {
        ObserverView observer = _views[view];
        if (observer.Mode == CameraMode.Surface && observer.Navigation is null && snapshot.SolarSystem is { } solar)
        {
            var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
            var surface = EarthSurfaceLighting.Sample(solar, camera.Site, DeltaT);
            return solar.Earth.PositionKilometers + surface.PositionKilometers;
        }
        var pose = observer.Navigation ?? PresetCamera(observer, snapshot);
        return pose.AnchorKilometers + pose.ResolvePlacement(snapshot, DeltaT).OffsetKilometers;
    }
}
