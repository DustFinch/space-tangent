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

    internal void SetScriptAimTarget(int view, ObjectId? target)
    {
        if (target is { } id && World.Catalogue.Find(id) is null)
            throw new ArgumentException("The script aim target is unavailable.", nameof(target));
        _views[view] = _views[view] with { ScriptAimTarget = target };
    }

    private void UpdateScriptAim(int view, WorldSnapshot snapshot)
    {
        var observer = _views[view];
        if (observer.ScriptAimTarget is not { } id || InShip(view) ||
            !World.TryNavigationState(id, snapshot, out var target, DeltaT))
            return;
        Vector3d sight = target.PositionKilometers - ScriptObserverEye(view, snapshot);
        if (sight.Length < NavigationCamera.MinRange)
            return;
        Vector3d forward = sight / sight.Length;
        if (observer.Mode == CameraMode.Surface && observer.Navigation is null && snapshot.SolarSystem is { } solar)
        {
            var site = observer.EarthObserver ?? EarthObserverCamera.Moscow;
            var local = EarthSurfaceLighting.Sample(solar, site.Site, DeltaT).EastNorthUp.Transpose().Transform(forward);
            const double degrees = 180 / Math.PI;
            double azimuth = Math.Atan2(local.X, local.Y) * degrees;
            _views[view] = observer with { EarthObserver = site with
            {
                AzimuthDegrees = (azimuth + 360) % 360,
                ElevationDegrees = Math.Asin(Math.Clamp(local.Z, -1, 1)) * degrees
            } };
            return;
        }

        var pose = ScriptCamera(view, snapshot);
        var world = pose.ResolvePlacement(snapshot, DeltaT);
        Vector3d right = Vector3d.Cross(forward, world.Up);
        if (right.Length < 1e-8)
            right = Vector3d.Cross(forward, Vector3d.Cross(world.Forward, world.Up));
        Vector3d up = Vector3d.Cross(right / right.Length, forward);
        var placement = world;
        if (pose.TryFrameBasis(snapshot, DeltaT, out var basis))
        {
            var inverse = basis.Transpose();
            placement = pose.Placement!.Value;
            forward = inverse.Transform(forward);
            up = inverse.Transform(up);
        }
        _views[view] = observer with { Navigation = pose with
        {
            Placement = placement with { Forward = forward, Up = up }
        } };
    }

    internal bool MoveClassicView(int view, Vector3d localOffset, WorldSnapshot snapshot)
    {
        if (InShip(view))
            return false;
        var camera = ScriptCamera(view, snapshot);
        var placement = camera.ResolvePlacement(snapshot, DeltaT);
        Vector3d displacement = CameraAxis(placement, localOffset);
        SetClassicPose(view, snapshot, ScriptObserverEye(view, snapshot) + displacement, null);
        return true;
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
