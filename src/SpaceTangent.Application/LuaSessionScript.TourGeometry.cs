// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private const double Radians = Math.PI / 180;

    private void BindTourGeometry(Table api)
    {
        Bind(api, "legacygoto", (_, a) => BeginLookFlight(Number(a[0]), Number(a[1]),
            Vector(a, 2), Text(a[5]), null));
        Bind(api, "legacylonglat", (_, a) => BeginLookFlight(Number(a[0]), Number(a[1]),
            Vector(a, 2), "bodyfixed", (Number(a[5]), Number(a[6]))));
        Bind(api, "legacyrelativepose", (_, a) => BeginRelativePose(Number(a[0]), Vector(a, 1), Vector(a, 4)));
        Bind(api, "legacyabsolutepose", (_, a) => BeginAbsolutePose(Number(a[0]),
            new(SceneLink.ReadCoordinate(Text(a[1])), SceneLink.ReadCoordinate(Text(a[2])), SceneLink.ReadCoordinate(Text(a[3]))),
            Number(a[4]), Vector(a, 5)));
        Bind(api, "legacyposition", (_, a) => SetClassicPosition(
            new(SceneLink.ReadCoordinate(Text(a[0])), SceneLink.ReadCoordinate(Text(a[1])), SceneLink.ReadCoordinate(Text(a[2])))));
        Bind(api, "legacypositionkm", (_, a) => SetClassicPosition(
            Vector(a, 0) * AstronomicalUnits.KilometersPerLightYear + Vector(a, 3)));
        Bind(api, "legacyorientation", (_, a) => SetClassicOrientation(Number(a[0]), Vector(a, 1)));
        Bind(api, "legacyorientationaxis", (_, a) =>
        {
            var (sine, cosine) = Math.SinCos(Number(a[3]) * Radians / 2);
            return SetClassicOrientation(cosine, Unit(Vector(a, 0)) * sine);
        });
        Bind(api, "turnaxis", (_, a) => Change(() =>
            _session.TurnScriptView(View, Vector(a, 0), Number(a[3]), Boolean(a[4]), NativeSnapshot())));
        Bind(api, "legacylookback", (_, _) => Change(() => _session.LookBackForScript(View, NativeSnapshot())));
        Bind(api, "travelling", (_, _) => DynValue.NewBoolean(_session.IsFlying(View)));
        Bind(api, "legacymove", (_, a) =>
        {
            Vector3d velocity = Vector(a, 0);
            double duration = Number(a[3]), seconds = Number(a[4]);
            Vector3d distance = velocity * duration;
            if (duration < 0 || seconds < 0 || !distance.IsFinite || distance.Length > 1e14)
                throw new ScriptRuntimeException("Invalid timed camera movement.");
            return WaitFor(seconds, moveVelocity: seconds > 0 ? distance / seconds : default);
        });
        Bind(api, "legacydolly", (_, a) =>
        {
            double amount = Number(a[0]), seconds = Number(a[1]);
            double rate = seconds > 0 ? amount / seconds : 0;
            if (seconds < 0 || !double.IsFinite(rate))
                throw new ScriptRuntimeException("Invalid timed distance change.");
            return WaitFor(seconds, rate);
        });
    }

    private DynValue BeginLookFlight(double seconds, double multiple, Vector3d authoredUp, string frame,
        (double Longitude, double Latitude)? location)
    {
        if (_session[View].Selection is not { } target)
            return DynValue.Nil;
        if (multiple is <= 0 or > 1e8)
            throw new ScriptRuntimeException("Tour flight distance is outside the supported range.");
        var snapshot = NativeSnapshot();
        if (!_session.World.TryNavigationState(target, snapshot, out var center, _session.DeltaT))
            return DynValue.Nil;
        var pose = _session.ScriptCamera(View, snapshot);
        var current = pose.ResolvePlacement(snapshot, _session.DeltaT);
        var eye = _session.ScriptObserverEye(View, snapshot);
        double range = Math.Clamp(_session.World.TargetRadiusKilometers(target) * multiple, NavigationCamera.MinRange, NavigationCamera.MaxRange);
        Vector3d up = frame.ToLowerInvariant() switch
        {
            "observer" or "observerlocal" => SimulationSession.CameraAxis(current, authoredUp),
            "universal" or "ecliptic" or "ecliptical" => SceneLink.FromEcliptic(authoredUp),
            "equatorial" => Equatorial(authoredUp),
            "geographic" or "bodyfixed" or "planetographic" => BodyFrame().Transform(Equatorial(authoredUp)),
            _ => throw new ScriptRuntimeException("Unknown up-vector reference frame.")
        };

        Vector3d outward = eye - center.PositionKilometers;
        if (location is { } coordinates)
        {
            if (coordinates.Latitude is < -90 or > 90)
                throw new ScriptRuntimeException("Tour latitude must be between -90 and 90 degrees.");
            var (sinLon, cosLon) = Math.SinCos(coordinates.Longitude * Radians);
            var (sinLat, cosLat) = Math.SinCos(coordinates.Latitude * Radians);
            outward = BodyFrame().Transform(new(cosLat * cosLon, cosLat * sinLon, sinLat));
        }
        if (outward.Length <= 0)
            outward = current.Forward * -1;
        Vector3d forward = outward / -outward.Length;
        Vector3d horizontal = Vector3d.Cross(forward, Unit(up));
        if (horizontal.Length < 1e-10)
            horizontal = Vector3d.Cross(forward, Math.Abs(forward.Z) < .95 ? new(0, 0, 1) : new(1, 0, 0));
        var destination = new NavigationCamera(center.PositionKilometers, range, 0, 0, target, true)
            { Placement = new(forward * -range, forward, Unit(Vector3d.Cross(horizontal, forward))) };
        return Change(() => _session.FlyToScriptPose(View, destination, seconds, snapshot));

        Basis3d BodyFrame() => NavigationCamera.TryBodyBasis(snapshot, target, _session.DeltaT, out var basis) ? basis :
            throw new ScriptRuntimeException("The selected body has no rotating frame at this epoch.");
    }

    private DynValue BeginRelativePose(double seconds, Vector3d offset, Vector3d rotation)
    {
        if (!(offset.Length > 0))
            throw new ScriptRuntimeException("A relative position needs a nonzero offset.");
        var snapshot = NativeSnapshot();
        var pose = _session.ScriptCamera(View, snapshot);
        ObjectId? reference = pose is { Tracking: true } ? pose.Target : _session[View].Selection;
        if (reference is not { } target || !_session.World.TryNavigationState(target, snapshot, out var state, _session.DeltaT))
            return DynValue.Nil;
        bool local = pose.TryFrameBasis(snapshot, _session.DeltaT, out var basis);
        Vector3d offsetWorld = Coordinates(offset);
        var destination = new NavigationCamera(state.PositionKilometers, offsetWorld.Length, 0, 0, target, true)
        {
            Placement = new(offsetWorld, Coordinates(Rotate(new(0, 0, -1))), Coordinates(Rotate(new(0, 1, 0))))
        };
        return Change(() => _session.FlyToScriptPose(View, destination, seconds, snapshot));

        Vector3d Coordinates(Vector3d value) => local ? basis.Transform(Equatorial(value)) : SceneLink.FromEcliptic(value);
        Vector3d Rotate(Vector3d value)
        {
            value = NavigationPlacement.Rotate(value, new(1, 0, 0), -rotation.X * Radians);
            value = NavigationPlacement.Rotate(value, new(0, 1, 0), -rotation.Y * Radians);
            return NavigationPlacement.Rotate(value, new(0, 0, 1), -rotation.Z * Radians);
        }
    }

    private DynValue SetClassicPosition(Vector3d position)
    {
        var snapshot = NativeSnapshot();
        if (!snapshot.TryState(SolarSystemSample.SunId, out var sun))
            return DynValue.Nil;
        Vector3d eye = sun.PositionKilometers + SceneLink.FromEcliptic(position);
        return Change(() => _session.SetClassicPose(View, snapshot, eye, null));
    }

    private DynValue SetClassicOrientation(double w, Vector3d quaternion)
    {
        var (forward, up) = AbsoluteAttitude(w, quaternion);
        return Change(() => _session.SetClassicPose(View, NativeSnapshot(), null, (forward, up)));
    }

    private DynValue BeginAbsolutePose(double seconds, Vector3d position, double w, Vector3d quaternion)
    {
        var (forward, up) = AbsoluteAttitude(w, quaternion);
        var snapshot = NativeSnapshot();
        if (!snapshot.TryState(SolarSystemSample.SunId, out var sun))
            return DynValue.Nil;
        Vector3d eye = sun.PositionKilometers + SceneLink.FromEcliptic(position);
        var pose = _session.ScriptCamera(View, snapshot);
        var destination = new NavigationCamera(eye, pose.RangeKilometers, 0, 0, null, false)
        {
            Placement = new(default, forward, up)
        };
        return Change(() => _session.FlyToScriptPose(View, destination, seconds, snapshot));
    }

    private static (Vector3d Forward, Vector3d Up) AbsoluteAttitude(double w, Vector3d quaternion)
    {
        double length = Math.Sqrt(w * w + Vector3d.Dot(quaternion, quaternion));
        if (!double.IsFinite(length) || length < .00001)
            throw new ScriptRuntimeException("The saved camera quaternion is invalid.");
        w /= length;
        quaternion /= -length;
        return (SceneLink.FromEcliptic(Rotate(new(0, 0, -1))), SceneLink.FromEcliptic(Rotate(new(0, 1, 0))));

        Vector3d Rotate(Vector3d value)
        {
            Vector3d cross = Vector3d.Cross(quaternion, value);
            return value + cross * (2 * w) + Vector3d.Cross(quaternion, cross) * 2;
        }
    }

    private static Vector3d Vector(CallbackArguments args, int index) =>
        new(Number(args[index]), Number(args[index + 1]), Number(args[index + 2]));

    private static Vector3d Unit(Vector3d vector) => vector.IsFinite && vector.Length > 0 ? vector / vector.Length :
        throw new ScriptRuntimeException("The vector must be finite and nonzero.");

    private static Vector3d Equatorial(Vector3d legacy) => new(legacy.X, -legacy.Z, legacy.Y);
}
