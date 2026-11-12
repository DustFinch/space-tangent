// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public enum ShipPilot { Manual, Approach, Orbit }

public sealed record ShipView(Vector3d Position, Vector3d Forward, Vector3d Up, bool Cockpit, ShipPilot Pilot,
    double SpeedKilometersPerSecond, ObjectId? Target, IReadOnlyCollection<Vector3d> Trail);

public sealed partial class SimulationSession
{
    public const double ShipRadiusKilometers = .0232;
    private const double MaximumShipSpeed = 1e13;
    private const double ShipApproachRate = .8;
    private const double ShipOrbitSeconds = 90;
    private const int MaximumTrailPoints = 256;
    private readonly Dictionary<int, ShipFlight> _ships = [];

    private sealed class ShipFlight(Vector3d position, NavigationPlacement attitude)
    {
        public Vector3d Position = position;
        public NavigationPlacement Attitude = attitude with { OffsetKilometers = default };
        public readonly Queue<Vector3d> Trail = new();
        public double TrailTime;
        public double Throttle, Speed;
        public double ViewYaw, ViewPitch;
        public double ViewDistance = 6 * ShipRadiusKilometers;
        public bool Cockpit;
        public ShipPilot Pilot;
        public ObjectId? Target;
        public Vector3d Offset, OrbitAxis;
        public double Standoff;
    }

    public bool HasShips => _ships.Count > 0;
    public bool InShip(int view) => _ships.ContainsKey(ViewIdentity(view));

    public ShipView? Ship(int view)
    {
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return null;
        return new(ship.Position, ship.Attitude.Forward, ship.Attitude.Up, ship.Cockpit, ship.Pilot,
            ship.Speed, ship.Target ?? _views[view].Selection, ship.Trail);
    }

    public void BoardShip(int view)
    {
        if (InShip(view))
            return;
        StartFreeFlight(view);
        NavigationCamera camera = _views[view].Navigation!.Value;
        NavigationPlacement attitude = camera.Placement!.Value;
        var ship = new ShipFlight(camera.AnchorKilometers + attitude.Forward * (6 * ShipRadiusKilometers), attitude);
        _ships.Add(ViewIdentity(view), ship);
        PlaceShipCamera(view, ship);
    }

    public bool LeaveShip(int view) => _ships.Remove(ViewIdentity(view));

    public void ToggleShipCockpit(int view)
    {
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return;
        ship.Cockpit = !ship.Cockpit;
        PlaceShipCamera(view, ship);
    }

    public void SetShipThrottle(int view, double speed)
    {
        if (!double.IsFinite(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return;
        ship.Pilot = ShipPilot.Manual;
        ship.Target = null;
        ship.Speed = ship.Throttle = speed > 0 ? Math.Clamp(speed, .001, MaximumShipSpeed) : 0;
    }

    public void FlyShipManually(int view, double throttleExponent, double yaw, double pitch, double roll)
    {
        if (!double.IsFinite(throttleExponent + yaw + pitch + roll))
            throw new ArgumentOutOfRangeException(nameof(throttleExponent));
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return;
        if (throttleExponent != 0)
        {
            double factor = Math.Pow(10, Math.Clamp(throttleExponent, -3, 3));
            SetShipThrottle(view, ship.Throttle > 0 ? ship.Throttle * factor : throttleExponent > 0 ? 1 : 0);
        }
        if ((yaw, pitch, roll) == (0d, 0d, 0d))
            return;
        if (ship.Pilot != ShipPilot.Manual)
            SetShipThrottle(view, ship.Speed);
        var steered = ship.Attitude.Turn(yaw, pitch, orbit: false);
        ship.Attitude = steered with { Up = NavigationPlacement.Rotate(steered.Up, steered.Forward, roll) };
    }

    public bool SetShipPilot(int view, ShipPilot pilot)
    {
        if (!Enum.IsDefined(pilot) || !_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return false;
        if (pilot == ShipPilot.Manual)
        {
            SetShipThrottle(view, ship.Speed);
            return true;
        }
        if (!TrySelectedState(view, out ObjectId target, out _, out var state))
            return false;
        Vector3d offset = ship.Position - state.PositionKilometers;
        if (offset.Length <= NavigationCamera.MinRange)
            return false;
        ship.Offset = offset;
        ship.Standoff = Math.Max(World.TargetRadiusKilometers(target) * 4, 1);
        Vector3d radial = offset / offset.Length;
        Vector3d axis = ship.Attitude.Up - radial * Vector3d.Dot(radial, ship.Attitude.Up);
        if (axis.Length < 1e-8)
            axis = Vector3d.Cross(radial, ship.Attitude.Forward);
        ship.OrbitAxis = axis / axis.Length;
        ship.Pilot = pilot;
        ship.Target = target;
        return true;
    }

    public void UpdateShip(int view, WorldSnapshot snapshot, double wallSeconds)
    {
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return;
        double dt = Math.Clamp(wallSeconds, 0, .25);
        if (ship.Target is { } id && World.TryNavigationState(id, snapshot, out var target, DeltaT))
        {
            Vector3d before = ship.Offset;
            double distance = before.Length;
            Vector3d forward;
            Vector3d up = ship.Attitude.Up;
            if (ship.Pilot == ShipPilot.Approach)
            {
                double way = Math.Max(0, distance - ship.Standoff);
                double left = Math.Max(way * Math.Exp(-ShipApproachRate * dt), way - MaximumShipSpeed * dt);
                ship.Offset *= way > 0 ? (ship.Standoff + left) / distance : 1;
                forward = ship.Offset * (-1 / ship.Offset.Length);
            }
            else
            {
                double radius = ship.Standoff + (distance - ship.Standoff) * Math.Exp(-2 * dt);
                ship.Offset = NavigationPlacement.Rotate(before / distance, ship.OrbitAxis, Math.Tau * dt / ShipOrbitSeconds) * radius;
                up = ship.OrbitAxis;
                forward = Vector3d.Cross(up, ship.Offset / radius);
            }
            ship.Position = target.PositionKilometers + ship.Offset;
            if (dt > 0)
            {
                ship.Speed = (ship.Offset - before).Length / dt;
                var desired = Aim(forward, up, ship.Attitude.Forward);
                ship.Attitude = ship.Attitude.Interpolate(desired, Math.Min(1, 3 * dt));
            }
        }
        else
        {
            if (ship.Target.HasValue)
                SetShipThrottle(view, ship.Speed);
            ship.Position += ship.Attitude.Forward * (ship.Throttle * dt);
        }
        ship.TrailTime += dt;
        double movement = ship.Trail.Count == 0 ? double.PositiveInfinity : (ship.Position - ship.Trail.Last()).Length;
        if (movement > 0 && (ship.TrailTime >= .25 || movement >= 20 * ShipRadiusKilometers))
        {
            if (ship.Trail.Count == MaximumTrailPoints)
                ship.Trail.Dequeue();
            ship.Trail.Enqueue(ship.Position);
            ship.TrailTime = 0;
        }
        PlaceShipCamera(view, ship);
    }

    private bool AdjustShipCamera(int view, double yaw, double pitch, double zoom)
    {
        if (!_ships.TryGetValue(ViewIdentity(view), out ShipFlight? ship))
            return false;
        ship.ViewYaw = Math.IEEERemainder(ship.ViewYaw + yaw, Math.Tau);
        ship.ViewPitch = Math.Clamp(ship.ViewPitch + pitch, -1.5, 1.5);
        ship.ViewDistance = Math.Clamp(ship.ViewDistance * zoom, ShipRadiusKilometers * 2, ShipRadiusKilometers * 500);
        PlaceShipCamera(view, ship);
        return true;
    }

    private void PlaceShipCamera(int view, ShipFlight ship)
    {
        var nose = ship.Attitude;
        var look = nose.Turn(ship.ViewYaw, ship.ViewPitch, orbit: false);
        Vector3d eye = ship.Cockpit ? ship.Position + (nose.Up * .3 + nose.Forward * .55) * ShipRadiusKilometers
            : ship.Position - look.Forward * ship.ViewDistance;
        var camera = new NavigationCamera(eye, ship.ViewDistance, 0, 0, null, false) { Placement = look };
        if (camera.IsValid())
            _views[view] = _views[view] with { Navigation = camera };
    }

    private static NavigationPlacement Aim(Vector3d forward, Vector3d up, Vector3d fallback)
    {
        Vector3d right = Vector3d.Cross(forward, up);
        if (right.Length < 1e-8)
            right = Vector3d.Cross(forward, fallback);
        if (right.Length < 1e-8)
            right = Vector3d.Cross(forward, Math.Abs(forward.X) < .9 ? new(1, 0, 0) : new(0, 1, 0));
        return new(default, forward, Vector3d.Cross(right / right.Length, forward));
    }
}
