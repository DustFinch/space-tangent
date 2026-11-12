// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public static partial class SceneLink
{
    internal static SimulationScenario? RestoreFavorite(ObserverFavorite favorite, SimulationWorld world, DeltaTModel deltaT)
    {
        if (favorite.JulianDay < world.Ephemeris.Start.ToJulianDate() ||
            favorite.JulianDay > world.Ephemeris.End.ToJulianDate())
            return null;
        JulianInstant epoch = JulianInstant.FromJulianDate(favorite.JulianDay, TimeScale.Tdb);
        WorldSnapshot snapshot = world.Graph.Evaluate(epoch);
        if (!snapshot.TryState(SolarSystemSample.SunId, out CartesianState sun))
            return null;
        ObjectId selected = SolarSystemSample.SunId;
        if (!string.IsNullOrWhiteSpace(favorite.Selection))
        {
            try { selected = ResolveObject(favorite.Selection, world.Catalogue); }
            catch (InvalidDataException) { }
        }

        Vector3d eye = sun.PositionKilometers + FromEcliptic(favorite.PositionKilometers);
        var inertial = new NavigationPlacement(default, FromEcliptic(Rotate(new(0, 0, -1))), FromEcliptic(Rotate(new(0, 1, 0))));
        bool follow = favorite.CoordinateSystem is "ecliptical" or "geographic";
        bool bodyFixed = favorite.CoordinateSystem == "geographic";
        if (!TryRestoreAbsoluteCamera(world, snapshot, deltaT, eye, inertial, selected, follow ? selected : null,
                bodyFixed, NavigationFrame.Inertial, null, out NavigationCamera camera))
            return null;
        var observer = new ObserverView(epoch, CameraMode.Solar, selected, true) { Navigation = camera, PlaybackRate = 1 };
        return new([observer], true, 0, 0, deltaT);

        Vector3d Rotate(Vector3d direction)
        {
            var (sin, cos) = Math.SinCos(-favorite.AngleRadians);
            Vector3d axis = favorite.Axis;
            return direction * cos + Vector3d.Cross(axis, direction) * sin +
                axis * (Vector3d.Dot(axis, direction) * (1 - cos));
        }
    }

    private static bool TryRestoreAbsoluteCamera(SimulationWorld world, WorldSnapshot snapshot, DeltaTModel deltaT,
        Vector3d eye, NavigationPlacement attitude, ObjectId selected, ObjectId? target, bool bodyFixed,
        NavigationFrame frame, ObjectId? reference, out NavigationCamera camera)
    {
        bool hasState = world.TryNavigationState(target ?? selected, snapshot, out CartesianState body);
        if (target.HasValue && !hasState)
        {
            camera = default;
            return false;
        }
        Vector3d offset = hasState ? eye - body.PositionKilometers : default;
        Vector3d anchor = target.HasValue ? body.PositionKilometers : eye;
        NavigationPlacement placement = attitude with { OffsetKilometers = target.HasValue ? offset : default };
        camera = new(anchor, Math.Max(NavigationCamera.MinRange, hasState ? offset.Length : 100_000),
            0, 0, target, target.HasValue);
        if (bodyFixed || frame != NavigationFrame.Inertial)
        {
            camera = camera with { Placement = placement with { BodyFixed = bodyFixed, Frame = frame, Reference = reference } };
            if (!camera.TryFrameBasis(snapshot, deltaT, out Basis3d basis))
                return false;
            placement = placement.Transform(basis.Transpose(), bodyFixed) with { Frame = frame, Reference = reference };
        }
        camera = camera with { Placement = placement };
        return camera.IsValid();
    }
}
