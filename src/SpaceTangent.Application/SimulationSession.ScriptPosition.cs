// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    internal void SetClassicPose(int view, WorldSnapshot snapshot, Vector3d? eye, (Vector3d Forward, Vector3d Up)? attitude)
    {
        var observer = _views[view];
        var pose = ScriptCamera(view, snapshot);
        var world = pose.ResolvePlacement(snapshot, DeltaT);
        Vector3d position = eye ?? ScriptObserverEye(view, snapshot);
        Vector3d forward = attitude?.Forward ?? world.Forward;
        Vector3d up = attitude?.Up ?? world.Up;
        if (observer.Navigation is null || !pose.Tracking)
        {
            FlyToScriptPose(view, pose with
            {
                AnchorKilometers = position,
                Target = null,
                Tracking = false,
                Placement = new(default, forward, up)
            }, 0, snapshot);
            return;
        }

        var local = pose.Placement ?? world;
        Vector3d offset = eye.HasValue ? position - pose.AnchorKilometers : world.OffsetKilometers;
        if (pose.TryFrameBasis(snapshot, DeltaT, out var basis))
        {
            var inverse = basis.Transpose();
            offset = inverse.Transform(offset);
            forward = inverse.Transform(forward);
            up = inverse.Transform(up);
        }
        var destination = pose with
        {
            RangeKilometers = eye.HasValue ? Math.Max(NavigationCamera.MinRange, offset.Length) : pose.RangeKilometers,
            Placement = local with { OffsetKilometers = offset, Forward = forward, Up = up }
        };
        FlyToScriptPose(view, destination, 0, snapshot);
    }

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
