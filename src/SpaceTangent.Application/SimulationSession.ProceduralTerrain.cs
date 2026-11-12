// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    private const double MinimumProceduralClearance = .05;

    /// <summary>Flies to an elevated viewpoint on the near side of an authored procedural world.</summary>
    public bool ApproachProceduralSurface(int view)
    {
        if (InShip(view) || _views[view].Selection is not { } selected || SurfaceTerrain(selected) is not { } terrain ||
            !TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out var state) ||
            !snapshot.TryOrientation(target, out var orientation))
            return false;

        NavigationCamera camera = ScriptCamera(view, snapshot);
        NavigationPlacement current = camera.ResolvePlacement(snapshot, DeltaT);
        Vector3d relative = (camera.AnchorKilometers - state.PositionKilometers) + current.OffsetKilometers;
        Basis3d fixedToWorld = orientation.ToWorld;
        Vector3d outward = relative.Length > 0 ? relative / relative.Length : fixedToWorld.X;
        Vector3d native = ToNative(fixedToWorld.Transpose().Transform(outward));
        double ground = terrain.GroundRadiusKilometers(native);
        double range = ground + Math.Clamp(.02 * ground, 5, 150);
        Vector3d local = FromNative(native);
        Vector3d north = new(0, 0, 1);
        if (Math.Abs(Vector3d.Dot(local, north)) > .99)
            north = new(1, 0, 0);
        Vector3d up = north - local * Vector3d.Dot(north, local);
        var destination = new NavigationCamera(state.PositionKilometers, range, 0, 0, target, true)
        {
            Placement = new(local * range, local * -1, up / up.Length, BodyFixed: true)
        };
        FlyToScriptPose(view, destination, FlightDuration, snapshot);
        _flights[view] = _flights[view]! with { SurfaceStartOffset = relative.Length > 0 ? relative : outward * range };
        return true;
    }

    private static ProceduralTerrain? SurfaceTerrain(ObjectId target) =>
        DemoWorlds.Find(target) is { } body && DemoWorlds.CreateSurface(body) is { HasSurface: true } surface
            ? new(surface, body.PolarRadiusKilometers) : null;

    private static NavigationCamera ClampProceduralClearance(NavigationCamera camera)
    {
        if (camera is not { Target: { } target, Placement: { BodyFixed: true } placement } ||
            SurfaceTerrain(target) is not { } terrain)
            return camera;

        double distance = placement.OffsetKilometers.Length;
        if (!(distance > 0))
            return camera;
        Vector3d direction = placement.OffsetKilometers / distance;
        double minimum = terrain.GroundRadiusKilometers(ToNative(direction)) + MinimumProceduralClearance;
        return distance < minimum ? camera with
        {
            RangeKilometers = minimum,
            Placement = placement with { OffsetKilometers = direction * minimum }
        } : camera;
    }

    private static Vector3d SurfaceFlightOffset(Vector3d start, Vector3d end, double t)
    {
        double startRange = start.Length, endRange = end.Length;
        Vector3d from = start / startRange, to = end / endRange;
        double cosine = Math.Clamp(Vector3d.Dot(from, to), -1, 1);
        Vector3d axis = Vector3d.Cross(from, to);
        if (axis.Length < 1e-12 && cosine < 0)
            axis = Vector3d.Cross(from, Math.Abs(from.Z) > .9 ? new(1, 0, 0) : new(0, 0, 1));
        Vector3d radial = axis.Length < 1e-12 ? from :
            NavigationPlacement.Rotate(from, axis / axis.Length, Math.Acos(cosine) * t);
        return radial * InterpolateRange(startRange, endRange, t);
    }

    private static Vector3d ToNative(Vector3d local) => new(local.X, local.Z, -local.Y);
    private static Vector3d FromNative(Vector3d native) => new(native.X, -native.Z, native.Y);
}
