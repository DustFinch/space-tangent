// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    public void StartFreeFlight(int view)
    {
        LeaveShip(view);
        ObserverView observer = _views[view];
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        NavigationCamera camera = observer.Navigation ?? PresetCamera(observer, snapshot);
        NavigationPlacement placement = camera.ResolvePlacement(snapshot, DeltaT);
        CancelFlight(view);
        _views[view] = observer with
        {
            Navigation = camera with
            {
                AnchorKilometers = camera.AnchorKilometers + placement.OffsetKilometers,
                Placement = placement with { OffsetKilometers = default },
                Target = null,
                Tracking = false
            }
        };
    }

    public bool CanMoveFreeFlight(int view) => !InShip(view) && !IsFlying(view) &&
        _views[view].Navigation is { Tracking: false, Placement.BodyFixed: false };

    public bool MoveFreeFlight(int view, double right, double up, double forward, double elapsed, bool boost)
    {
        double length = new Vector3d(right, up, forward).Length;
        if (!(length > 0) || !double.IsFinite(length) || !(elapsed > 0) || !CanMoveFreeFlight(view))
            return false;
        ObserverView observer = _views[view];
        if (observer.Navigation is not { Tracking: false, Placement: { BodyFixed: false } placement } camera)
            return false;
        double distance = camera.RangeKilometers;
        if (observer.Selection is { } target &&
            World.TryNavigationState(target, World.Graph.Evaluate(observer.Epoch), out var state, DeltaT))
            distance = ((state.PositionKilometers - camera.AnchorKilometers) - placement.OffsetKilometers).Length;
        double speed = Math.Clamp(distance * .2, 1, 1e12);
        Vector3d axisRight = Vector3d.Cross(placement.Forward, placement.Up);
        Vector3d displacement = (axisRight * right + placement.Up * up + placement.Forward * forward) *
            (speed * Math.Min(elapsed, .25) * (boost ? 5 : 1) / length);
        NavigationCamera next = camera with { AnchorKilometers = camera.AnchorKilometers + displacement };
        if (!next.IsValid())
            return false;
        _views[view] = observer with { Navigation = next };
        return true;
    }

    /// <summary>Turns toward the selected object while leaving the camera at the same point.</summary>
    public bool FocusSelection(int view)
    {
        if (!TrySelectedState(view, out ObjectId selected, out WorldSnapshot snapshot, out var state))
            return false;
        ObserverView observer = _views[view];
        NavigationCamera camera = observer.Navigation ?? PresetCamera(observer, snapshot);
        NavigationPlacement current = camera.ResolvePlacement(snapshot, DeltaT);
        Vector3d eye = camera.AnchorKilometers + current.OffsetKilometers;
        Vector3d sight = state.PositionKilometers - eye;
        double distance = sight.Length;
        if (!(distance >= NavigationCamera.MinRange))
            return false;
        Vector3d forward = sight / distance;
        Vector3d right = Vector3d.Cross(forward, current.Up);
        if (right.Length < 1e-8)
            right = Vector3d.Cross(forward, Vector3d.Cross(current.Forward, current.Up));
        Vector3d up = Vector3d.Cross(right / right.Length, forward);

        NavigationPlacement start, end;
        if (camera.Placement is { UsesFrame: true } frame && camera.TryFrameBasis(snapshot, DeltaT, out Basis3d basis))
        {
            Basis3d inverse = basis.Transpose();
            start = frame;
            end = frame with { Forward = inverse.Transform(forward), Up = inverse.Transform(up) };
        }
        else
        {
            start = new(eye - state.PositionKilometers, current.Forward, current.Up);
            end = start with { Forward = forward, Up = up };
            camera = new(state.PositionKilometers, distance, 0, 0, selected, true);
        }
        camera = camera with { Placement = start };
        if (!camera.IsValid() || !end.IsValid(camera.RangeKilometers))
            return false;
        _flights[view] = new(camera, 0, .65, camera.RangeKilometers) { CenteringEnd = end };
        _trackingEpochs[view] = (camera.Target!.Value, snapshot.Epoch);
        _views[view] = observer with { Navigation = camera };
        return true;
    }

    public bool FollowRelativeFrame(int view, NavigationFrame frame, ObjectId? reference = null) =>
        _views[view].Selection is { } target && FollowRelativeFrame(view, target, frame, reference);

    internal bool FollowRelativeFrame(int view, ObjectId target, NavigationFrame frame, ObjectId? reference)
    {
        var snapshot = World.Graph.Evaluate(_views[view].Epoch);
        if ((reference ?? World.Catalogue.Find(target)?.Parent) is not { } other || other == target ||
            !NavigationCamera.TryRelativeBasis(snapshot, frame, target, other, out Basis3d basis) ||
            !World.TryNavigationState(target, snapshot, out var state))
            return false;
        return FollowFrame(view, snapshot, target, state.PositionKilometers, basis, false, frame, other);
    }

    public bool FollowBodyRotation(int view) =>
        _views[view].Selection is { } target && FollowBodyRotation(view, target);

    internal bool FollowBodyRotation(int view, ObjectId target)
    {
        var snapshot = World.Graph.Evaluate(_views[view].Epoch);
        if (!NavigationCamera.TryBodyBasis(snapshot, target, DeltaT, out Basis3d basis) ||
            !World.TryNavigationState(target, snapshot, out CartesianState state))
            return false;
        return FollowFrame(view, snapshot, target, state.PositionKilometers, basis, true, NavigationFrame.Inertial, null);
    }

    internal bool FollowScriptReference(int view, ObjectId target, NavigationFrame frame)
    {
        var snapshot = World.Graph.Evaluate(_views[view].Epoch);
        if (!World.TryNavigationState(target, snapshot, out var state, DeltaT))
            return false;
        Basis3d basis = Basis3d.Identity;
        if (frame == NavigationFrame.Equatorial && !NavigationCamera.TryMeanEquator(snapshot, target, out basis))
            return false;
        return FollowFrame(view, snapshot, target, state.PositionKilometers, basis, false, frame, null);
    }

    private bool FollowFrame(int view, WorldSnapshot snapshot, ObjectId target, Vector3d origin, Basis3d basis,
        bool bodyFixed, NavigationFrame frame, ObjectId? reference)
    {
        ObserverView observer = _views[view];
        NavigationCamera camera = observer.Navigation ?? PresetCamera(observer, snapshot);
        NavigationPlacement current = camera.ResolvePlacement(snapshot, DeltaT);
        Vector3d offset = camera.AnchorKilometers + current.OffsetKilometers - origin;
        NavigationPlacement local = (current with { OffsetKilometers = offset }).Transform(basis.Transpose(), bodyFixed) with
            { Frame = frame, Reference = reference };
        NavigationCamera next = camera with
        {
            AnchorKilometers = origin,
            RangeKilometers = Math.Max(NavigationCamera.MinRange, offset.Length),
            Target = target,
            Tracking = true,
            Placement = local
        };
        if (!next.IsValid())
            return false;
        LeaveShip(view);
        CancelFlight(view);
        _trackingEpochs[view] = (target, snapshot.Epoch);
        _views[view] = observer with { Navigation = next };
        return true;
    }
}
