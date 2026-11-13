// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    public bool OpenMissionViewpoint(int view, MissionViewpoint viewpoint)
    {
        var snapshot = World.Graph.Evaluate(_views[view].Epoch);
        if (!World.TryNavigationState(viewpoint.Center, snapshot, out var center, DeltaT))
            return false;
        var reference = new Vector3d(1, 0, 0);
        if (viewpoint.Reference is { } target && World.TryNavigationState(target, snapshot, out var state, DeltaT))
            reference = state.PositionKilometers - center.PositionKilometers;

        const double radians = Math.PI / 180;
        double obliquity = 23.4392911 * radians;
        Vector3d pole = viewpoint.EquatorialUp ? new(0, 0, 1) : new(0, -Math.Sin(obliquity), Math.Cos(obliquity));
        Vector3d x = reference - pole * Vector3d.Dot(reference, pole);
        if (x.Length < 1e-6)
            x = Vector3d.Cross(pole, new(1, 0, 0));
        x /= x.Length;
        Vector3d y = Vector3d.Cross(pole, x);
        var (sinA, cosA) = Math.SinCos(viewpoint.AzimuthDegrees * radians);
        var (sinE, cosE) = Math.SinCos(viewpoint.ElevationDegrees * radians);
        Vector3d outward = (x * cosA + y * sinA) * cosE + pole * sinE;
        outward /= outward.Length;
        Vector3d right = Vector3d.Cross(outward * -1, pole);
        right = right.Length < 1e-6 ? x : right / right.Length;
        Vector3d up = Vector3d.Cross(outward, right);
        double range = viewpoint.AltitudeKilometers + World.TargetRadiusKilometers(viewpoint.Center);
        var camera = new NavigationCamera(center.PositionKilometers, range, 0, 0, viewpoint.Center, false)
            { Placement = new(outward * range, outward * -1, up) };
        if (!camera.IsValid())
            return false;
        FlyToScriptPose(view, camera, 0, snapshot);
        SelectObject(view, viewpoint.Center);
        return true;
    }
}
