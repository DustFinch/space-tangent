// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    public void StartFreeFlight(int view)
    {
        ObserverView observer = _views[view];
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        NavigationCamera camera = observer.Navigation ?? PresetCamera(observer, snapshot);
        NavigationPlacement placement = camera.ResolvePlacement(snapshot);
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

    public bool FollowBodyRotation(int view)
    {
        ObserverView observer = _views[view];
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        if (observer.Selection is not { } target || !snapshot.TryOrientation(target, out var frame) ||
            !World.TryNavigationState(target, snapshot, out CartesianState state))
            return false;
        NavigationCamera camera = observer.Navigation ?? PresetCamera(observer, snapshot);
        NavigationPlacement current = camera.ResolvePlacement(snapshot);
        Vector3d offset = camera.AnchorKilometers + current.OffsetKilometers - state.PositionKilometers;
        NavigationPlacement local = (current with { OffsetKilometers = offset }).Transform(frame.ToWorld.Transpose(), true);
        NavigationCamera next = camera with
        {
            AnchorKilometers = state.PositionKilometers,
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
