// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    internal NavigationCamera ScriptCamera(int view, WorldSnapshot snapshot) =>
        _views[view].Navigation ?? PresetCamera(_views[view], snapshot);

    internal bool FlyToScriptPose(int view, NavigationCamera destination, double seconds, WorldSnapshot snapshot)
    {
        if (!destination.IsValid() || !double.IsFinite(seconds) || seconds is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(destination));
        var observer = _views[view];
        var start = ScriptCamera(view, snapshot);
        var placement = start.ResolvePlacement(snapshot, DeltaT);
        Vector3d eye = ScriptObserverEye(view, snapshot);
        var absolute = new NavigationCamera(eye, start.RangeKilometers, 0, 0, start.Target, false)
            { Placement = placement with { OffsetKilometers = default } };
        CancelFlight(view);
        _views[view] = observer with
        {
            Mode = CameraMode.Solar,
            Terrain = null,
            Navigation = seconds == 0 ? destination : absolute
        };
        if (seconds == 0)
            _trackingEpochs[view] = destination is { Tracking: true, Target: { } target } ? (target, snapshot.Epoch) : null;
        else
            _flights[view] = new(absolute, 0, seconds, destination.RangeKilometers) { ScriptDestination = destination };
        return true;
    }

    private void UpdateScriptFlight(int view, CameraFlight flight, NavigationCamera destination, WorldSnapshot snapshot,
        double elapsedSeconds)
    {
        if (destination is { Tracking: true, Target: { } target })
        {
            if (!World.TryNavigationState(target, snapshot, out var targetState, DeltaT))
            {
                CancelFlight(view);
                return;
            }
            destination = destination with { AnchorKilometers = targetState.PositionKilometers };
        }
        double elapsed = Math.Min(flight.Duration, flight.Elapsed + elapsedSeconds);
        if (elapsed >= flight.Duration)
        {
            _flights[view] = null;
            _trackingEpochs[view] = destination is { Tracking: true, Target: { } tracked } ? (tracked, snapshot.Epoch) : null;
            _views[view] = _views[view] with { Navigation = destination };
            return;
        }

        double fraction = elapsed / flight.Duration;
        double t = fraction * fraction * fraction * (fraction * (6 * fraction - 15) + 10);
        var end = destination.ResolvePlacement(snapshot, DeltaT);
        var start = flight.Start;
        Vector3d endpoint = destination.AnchorKilometers + end.OffsetKilometers;
        Vector3d eye = start.AnchorKilometers + (endpoint - start.AnchorKilometers) * t;
        if (flight.SurfaceStartOffset is { } approach)
        {
            Vector3d relative = SurfaceFlightOffset(approach, end.OffsetKilometers, t);
            // Keep the small eye offset separate from the interstellar position.
            start = start with { AnchorKilometers = destination.AnchorKilometers };
            eye = start.AnchorKilometers;
            end = end with { OffsetKilometers = relative };
        }
        var attitude = start.Placement!.Value.Interpolate(end with { OffsetKilometers = default }, t);
        if (flight.SurfaceStartOffset.HasValue)
            attitude = attitude with { OffsetKilometers = end.OffsetKilometers };
        double range = flight.SurfaceStartOffset.HasValue ? end.OffsetKilometers.Length :
            InterpolateRange(start.RangeKilometers, destination.RangeKilometers, t);
        _views[view] = _views[view] with { Navigation = start with { AnchorKilometers = eye, RangeKilometers = range, Placement = attitude } };
        _flights[view] = flight with { Elapsed = elapsed };
    }

    internal void LookBackForScript(int view, WorldSnapshot snapshot)
    {
        var observer = _views[view];
        if (observer.Navigation is not { } pose)
        {
            TurnScriptView(view, new(0, 1, 0), Math.PI, orbit: false, snapshot);
            return;
        }

        NavigationPlacement attitude = pose.Placement ?? pose.ResolvePlacement(snapshot, DeltaT);
        CancelFlight(view);
        _views[view] = observer with { Navigation = pose with
        {
            Placement = attitude with { Forward = attitude.Forward * -1 }
        } };
    }

    internal void TurnScriptView(int view, Vector3d cameraAxis, double radians, bool orbit, WorldSnapshot snapshot)
    {
        if (!cameraAxis.IsFinite || !(cameraAxis.Length > 0) || !double.IsFinite(radians))
            throw new ArgumentOutOfRangeException(nameof(cameraAxis));
        var observer = _views[view];
        var pose = ScriptCamera(view, snapshot);
        var placement = pose.ResolvePlacement(snapshot, DeltaT);
        Vector3d axis = CameraAxis(placement, cameraAxis / cameraAxis.Length);
        Vector3d eye = ScriptObserverEye(view, snapshot);
        var attitude = new NavigationPlacement(default, NavigationPlacement.Rotate(placement.Forward, axis, radians),
            NavigationPlacement.Rotate(placement.Up, axis, radians));
        ObjectId? pivot = pose.Target ?? observer.Selection;
        if (orbit)
        {
            if (pivot is not { } target || !World.TryNavigationState(target, snapshot, out var state, DeltaT))
                return;
            Vector3d offset = NavigationPlacement.Rotate(eye - state.PositionKilometers, axis, radians);
            if (offset.Length < NavigationCamera.MinRange || offset.Length > NavigationCamera.MaxRange)
                return;
            if (pose is { Tracking: true, Placement.UsesFrame: true })
            {
                SetClassicPose(view, snapshot, state.PositionKilometers + offset, (attitude.Forward, attitude.Up));
                return;
            }
            pose = new NavigationCamera(state.PositionKilometers, offset.Length, 0, 0, target, pose.Tracking)
                { Placement = attitude with { OffsetKilometers = offset } };
        }
        else
        {
            if (observer.Mode == CameraMode.Surface && observer.Navigation is null)
            {
                var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
                SetEarthObserver(view, camera.Turn(cameraAxis.Y / cameraAxis.Length * radians,
                    cameraAxis.X / cameraAxis.Length * radians));
            }
            else
                SetClassicPose(view, snapshot, null, (attitude.Forward, attitude.Up));
            return;
        }
        FlyToScriptPose(view, pose, 0, snapshot);
    }

    internal bool DollyScriptView(int view, double logarithmicChange, WorldSnapshot snapshot)
    {
        var observer = _views[view];
        if (InShip(view) || observer.Mode == CameraMode.Surface && observer.Navigation is null)
            return false;
        var pose = ScriptCamera(view, snapshot);
        var pivot = pose.Target ?? observer.Selection;
        if (pivot is not { } target || !World.TryNavigationState(target, snapshot, out var state, DeltaT))
            return false;
        var placement = pose.ResolvePlacement(snapshot, DeltaT);
        bool attached = pose is { Tracking: true, Target: { } id } && id == target;
        Vector3d offset = attached ? placement.OffsetKilometers : pose.AnchorKilometers + placement.OffsetKilometers - state.PositionKilometers;
        double distance = offset.Length;
        if (!(distance > 0))
            return false;
        double minimum = Math.Max(NavigationCamera.MinRange, World.TargetRadiusKilometers(target) * 1.02);
        double next = Math.Clamp(distance * Math.Exp(Math.Clamp(logarithmicChange, -700, 700)), minimum, 1e17);
        if (next == distance)
            return false;
        double scale = next / distance;
        if (attached)
        {
            var stored = pose.Placement ?? placement;
            pose = pose with
            {
                AnchorKilometers = state.PositionKilometers, RangeKilometers = next,
                Placement = stored with { OffsetKilometers = stored.OffsetKilometers * scale }
            };
        }
        else
        {
            pose = new NavigationCamera(state.PositionKilometers + offset * scale, next, 0, 0, pivot, false)
            {
                Placement = placement with { OffsetKilometers = default }
            };
        }
        return FlyToScriptPose(view, pose, 0, snapshot);
    }

    internal static Vector3d CameraAxis(NavigationPlacement pose, Vector3d axis) =>
        Vector3d.Cross(pose.Forward, pose.Up) * axis.X + pose.Up * axis.Y - pose.Forward * axis.Z;
}
