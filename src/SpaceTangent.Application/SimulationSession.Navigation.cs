// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    private sealed record CameraFlight(NavigationCamera Start, double Elapsed);

    private const double FlightDuration = 1.65;
    private readonly CameraFlight?[] _flights = new CameraFlight?[2];

    public bool IsFlying(int view) => _flights[view] is not null;

    public bool GoToSelection(int view)
    {
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out _))
            return false;
        ObserverView observer = _views[view];
        NavigationCamera start = observer.Navigation ?? PresetCamera(observer, snapshot);
        _flights[view] = new(start, 0);
        _views[view] = observer with { Navigation = start with { Target = target, Tracking = false } };
        return true;
    }

    public bool ToggleTracking(int view)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is { Tracking: true } tracked && tracked.Target == observer.Selection)
        {
            _views[view] = observer with { Navigation = tracked with { Tracking = false } };
            return true;
        }
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state))
            return false;
        observer = _views[view];
        NavigationCamera pose = observer.Navigation ?? PresetCamera(observer, snapshot);
        double minimum = MinimumRange(target);
        double range = pose.Target == target ? Math.Max(pose.RangeKilometers, minimum) : FramingRange(target);
        _flights[view] = null;
        _views[view] = observer with
        {
            Navigation = pose with { AnchorKilometers = state.PositionKilometers, RangeKilometers = range,
                Target = target, Tracking = true }
        };
        return true;
    }

    public bool CancelFlight(int view)
    {
        if (_flights[view] is null)
            return false;
        _flights[view] = null;
        return true;
    }

    public bool AdjustCamera(int view, double azimuth, double elevation, double zoom)
    {
        if (!double.IsFinite(azimuth) || !double.IsFinite(elevation) || !double.IsFinite(zoom) || zoom <= 0)
            return false;
        ObserverView observer = _views[view];
        if (observer.Navigation is not { } camera)
            return false;
        double minimum = camera.Target is { } target ? MinimumRange(target) : NavigationCamera.MinRange;
        CancelFlight(view);
        _views[view] = observer with { Navigation = camera.Adjust(azimuth, elevation, zoom, minimum) };
        return true;
    }

    /// <summary>Updates the view pose against the exact world snapshot used by its renderer.</summary>
    public void UpdateCamera(int view, WorldSnapshot snapshot, double elapsedSeconds)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is not { Target: { } target } pose)
            return;
        if (!snapshot.TryState(target, out var state))
        {
            CancelFlight(view);
            return;
        }
        Vector3d position = state.PositionKilometers;
        if (_flights[view] is { } flight)
        {
            double elapsed = Math.Min(FlightDuration, flight.Elapsed + elapsedSeconds);
            double t = elapsed / FlightDuration;
            double pan = Smooth(Math.Min(1, t * 1.8));
            double zoom = Smooth(t);
            double range = flight.Start.RangeKilometers * Math.Pow(FramingRange(target) / flight.Start.RangeKilometers, zoom);
            pose = pose with
            {
                AnchorKilometers = flight.Start.AnchorKilometers + (position - flight.Start.AnchorKilometers) * pan,
                RangeKilometers = range
            };
            _flights[view] = elapsed >= FlightDuration ? null : flight with { Elapsed = elapsed };
        }
        else if (pose.Tracking)
            pose = pose with { AnchorKilometers = position };
        _views[view] = observer with { Navigation = pose };
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double MinimumRange(ObjectId target)
    {
        double radius = NavigationCamera.BodyRadiusKilometers(target);
        return target == SolarSystemSample.EarthId ? radius + 20 : Math.Max(NavigationCamera.MinRange, 1.15 * radius);
    }
    private static double FramingRange(ObjectId target) => 4.2 * NavigationCamera.BodyRadiusKilometers(target);

    private bool TrySelectedState(int view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state)
    {
        ObserverView observer = _views[view];
        snapshot = World.Graph.Evaluate(observer.Epoch);
        target = observer.Selection.GetValueOrDefault();
        state = default;
        if (observer.Selection is null)
            return false;
        if (snapshot.TryState(target, out state))
            return true;
        ObjectId id = target;
        if (World.Satellites.FirstOrDefault(satellite => satellite.Id == id) is not { } satellite ||
            !Covers(World, satellite.Epoch))
            return false;
        snapshot = World.Graph.Evaluate(satellite.Epoch);
        if (!snapshot.TryState(id, out state))
            return false;
        Seek(view, satellite.Epoch);
        return true;
    }

    private static NavigationCamera PresetCamera(ObserverView observer, WorldSnapshot snapshot)
    {
        var solar = snapshot.SolarSystem!.Value;
        (Vector3d position, double range) = observer.Mode switch
        {
            CameraMode.Solar => (solar.Sun.PositionKilometers, 310_000_000),
            CameraMode.Lunar => (solar.Earth.PositionKilometers, 950_000),
            CameraMode.LunarTerrain => (solar.Moon.PositionKilometers, 5400),
            _ => (solar.Earth.PositionKilometers, 25_000)
        };
        return new(position, range, .35, 1.47, null, false);
    }
}
