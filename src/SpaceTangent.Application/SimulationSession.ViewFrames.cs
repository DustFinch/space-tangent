// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    public void StartFreeFlight(int view)
    {
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

    public bool MoveFreeFlight(int view, double right, double up, double forward)
    {
        if (!double.IsFinite(right) || !double.IsFinite(up) || !double.IsFinite(forward))
            return false;
        ObserverView observer = _views[view];
        if (observer.Navigation is not { Tracking: false, Placement: { BodyFixed: false } placement } camera)
            return false;
        Vector3d axisRight = Vector3d.Cross(placement.Forward, placement.Up);
        Vector3d displacement = (axisRight * right + placement.Up * up + placement.Forward * forward) *
            (camera.RangeKilometers * .13);
        NavigationCamera next = camera with { AnchorKilometers = camera.AnchorKilometers + displacement };
        if (!next.IsValid())
            return false;
        _views[view] = observer with { Navigation = next };
        return true;
    }

    public bool FollowRelativeFrame(int view, NavigationFrame frame, ObjectId? reference = null)
    {
        ObserverView observer = _views[view];
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        if (observer.Selection is not { } target ||
            (reference ?? World.Catalogue.Find(target)?.Parent) is not { } other || other == target ||
            !NavigationCamera.TryRelativeBasis(snapshot, frame, target, other, out Basis3d basis) ||
            !World.TryNavigationState(target, snapshot, out var state))
            return false;
        return FollowFrame(view, snapshot, target, state.PositionKilometers, basis, false, frame, other);
    }

    public bool FollowBodyRotation(int view)
    {
        ObserverView observer = _views[view];
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        if (observer.Selection is not { } target || !NavigationCamera.TryBodyBasis(snapshot, target, DeltaT, out Basis3d basis) ||
            !World.TryNavigationState(target, snapshot, out CartesianState state))
            return false;
        return FollowFrame(view, snapshot, target, state.PositionKilometers, basis, true, NavigationFrame.Inertial, null);
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
        CancelFlight(view);
        _trackingEpochs[view] = (target, snapshot.Epoch);
        _views[view] = observer with { Navigation = next };
        return true;
    }
}
