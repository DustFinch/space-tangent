// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    private sealed record CameraFlight(NavigationCamera Start, double Elapsed, double Duration, double TargetRange);

    private const double FlightDuration = 1.65;
    private readonly CameraFlight?[] _flights = new CameraFlight?[2];
    private readonly (ObjectId Target, JulianInstant Epoch)?[] _trackingEpochs = new (ObjectId, JulianInstant)?[2];

    public bool IsFlying(int view) => _flights[view] is not null;

    /// <summary>Enters the perspective view near Earth, retaining an existing space pose.</summary>
    public void OpenSpaceView(int view)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is not null)
            return;

        ObjectId earth = SolarSystemSample.EarthId;
        SolarSystemSample solar = World.Ephemeris.Sample(observer.Epoch);
        Vector3d sunward = solar.Sun.PositionKilometers - solar.Earth.PositionKilometers;
        Vector3d outward = sunward / sunward.Length + new Vector3d(0, 0, .7);
        double azimuth = Math.Atan2(outward.X, -outward.Y);
        double elevation = Math.Asin(outward.Z / outward.Length);
        CancelFlight(view);
        _trackingEpochs[view] = (earth, observer.Epoch);
        _views[view] = observer with
        {
            Mode = CameraMode.Solar,
            Terrain = null,
            Selection = earth,
            Navigation = new(solar.Earth.PositionKilometers, FramingRange(earth), azimuth, elevation, earth, true)
        };
    }

    public bool GoToSelection(int view) => GoToSelection(view, FlightDuration, null);

    internal bool GoToSelection(int view, double duration, double? range)
    {
        if (!double.IsFinite(duration) || duration is < 0 or > 300 || (range.HasValue && !double.IsFinite(range.Value)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out _))
            return false;
        ObserverView observer = _views[view];
        NavigationCamera start = (observer.Navigation ?? PresetCamera(observer, snapshot)) with { Placement = null };
        double targetRange = Math.Clamp(range ?? FramingRange(target), MinimumRange(target), NavigationCamera.MaxRange);
        _flights[view] = new(start, 0, Math.Max(duration, .001), targetRange);
        _views[view] = observer with { Navigation = start with { Target = target, Tracking = false } };
        return true;
    }

    public bool ToggleTracking(int view)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is { Tracking: true } tracked && tracked.Target == observer.Selection)
        {
            NavigationPlacement? placement = tracked.Placement is { BodyFixed: true }
                ? tracked.ResolvePlacement(World.Graph.Evaluate(observer.Epoch)) : tracked.Placement;
            _views[view] = observer with { Navigation = tracked with { Tracking = false, Placement = placement } };
            return true;
        }
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state))
            return false;
        observer = _views[view];
        NavigationCamera pose = observer.Navigation ?? PresetCamera(observer, snapshot);
        double minimum = MinimumRange(target);
        double range = pose.Target == target ? Math.Max(pose.RangeKilometers, minimum) : FramingRange(target);
        _flights[view] = null;
        _trackingEpochs[view] = (target, snapshot.Epoch);
        _views[view] = observer with
        {
            Navigation = pose with { AnchorKilometers = state.PositionKilometers, RangeKilometers = range,
                Target = target, Tracking = true, Placement = null }
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
        NavigationCamera next = camera.Adjust(azimuth, elevation, zoom, minimum);
        if (camera.Placement is { } placement)
            next = next with { Placement = placement.Turn(azimuth, elevation) with
                { OffsetKilometers = placement.OffsetKilometers * (next.RangeKilometers / camera.RangeKilometers) } };
        _views[view] = observer with { Navigation = next };
        return true;
    }

    /// <summary>Updates the view pose against the exact world snapshot used by its renderer.</summary>
    public void UpdateCamera(int view, WorldSnapshot snapshot, double elapsedSeconds)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is not { Target: { } target } pose)
            return;
        // Restoring a saved tracked view keeps its inertial anchor at the saved epoch.
        // Reevaluate the anchor when the clock advances, not on a render refresh or a reload.
        if (_flights[view] is null && (!pose.Tracking || _trackingEpochs[view] == (target, snapshot.Epoch)))
            return;
        if (!World.TryNavigationState(target, snapshot, out var state))
        {
            CancelFlight(view);
            return;
        }
        Vector3d position = state.PositionKilometers;
        if (_flights[view] is { } flight)
        {
            double elapsed = Math.Min(flight.Duration, flight.Elapsed + elapsedSeconds);
            double t = elapsed / flight.Duration;
            double pan = Smooth(Math.Min(1, t * 1.8));
            double destinationRange = flight.TargetRange;
            double range = InterpolateRange(flight.Start.RangeKilometers, destinationRange, Smooth(t));
            double travel = (position - flight.Start.AnchorKilometers).Length;
            double wide = Math.Min(NavigationCamera.MaxRange, travel * .35);
            if (travel > 30_000_000_000 && wide > Math.Max(flight.Start.RangeKilometers, destinationRange) * 2)
            {
                pan = Smooth(Math.Clamp((t - .15) / .65, 0, 1));
                range = t < .45
                    ? InterpolateRange(flight.Start.RangeKilometers, wide, Smooth(t / .45))
                    : InterpolateRange(wide, destinationRange, Smooth((t - .45) / .55));
            }
            pose = pose with
            {
                AnchorKilometers = flight.Start.AnchorKilometers + (position - flight.Start.AnchorKilometers) * pan,
                RangeKilometers = range
            };
            _flights[view] = elapsed >= flight.Duration ? null : flight with { Elapsed = elapsed };
        }
        else if (pose.Tracking)
        {
            pose = pose with { AnchorKilometers = position };
            _trackingEpochs[view] = (target, snapshot.Epoch);
        }
        _views[view] = observer with { Navigation = pose };
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
    private static double InterpolateRange(double start, double end, double fraction) =>
        start * Math.Pow(end / start, fraction);

    private double MinimumRange(ObjectId target)
    {
        double radius = World.TargetRadiusKilometers(target);
        return target == SolarSystemSample.EarthId ? radius + 20 : Math.Max(NavigationCamera.MinRange, 1.15 * radius);
    }
    private double FramingRange(ObjectId target) =>
        (target == PlanetCatalogue.SaturnId ? 6.4 : 4.2) * World.TargetRadiusKilometers(target);

    private bool TrySelectedState(int view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state)
    {
        ObserverView observer = _views[view];
        snapshot = World.Graph.Evaluate(observer.Epoch);
        target = observer.Selection.GetValueOrDefault();
        state = default;
        if (observer.Selection is null)
            return false;
        if (World.TryNavigationState(target, snapshot, out state))
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
            CameraMode.SolarOuter => (solar.Sun.PositionKilometers, 5_400_000_000),
            CameraMode.Lunar => (solar.Earth.PositionKilometers, 950_000),
            CameraMode.LunarTerrain => (solar.Moon.PositionKilometers, 5400),
            CameraMode.MarsTerrain when snapshot.TryState(PlanetCatalogue.MarsId, out CartesianState mars)
                => (mars.PositionKilometers, 12_000),
            _ => (solar.Earth.PositionKilometers, 25_000)
        };
        return new(position, range, .35, 1.47, null, false);
    }
}
