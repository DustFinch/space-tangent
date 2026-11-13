// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public readonly record struct CameraAutoOrbit(double PeriodSeconds, bool Clockwise, bool Enabled = false)
{
    public static CameraAutoOrbit Default => new(120, false);
    public bool IsValid() => PeriodSeconds is >= 20 and <= 1200;
}

public sealed partial class SimulationSession
{
    private sealed record CameraFlight(NavigationCamera Start, double Elapsed, double Duration, double TargetRange)
    {
        internal NavigationPlacement? CenteringEnd { get; init; }
        internal NavigationCamera? ScriptDestination { get; init; }
        internal Vector3d? SurfaceStartOffset { get; init; }
    }

    private const double FlightDuration = 1.65;
    private readonly List<CameraFlight?> _flights = [null];
    private readonly List<(ObjectId Target, JulianInstant Epoch)?> _trackingEpochs = [null];

    public bool AnyFlight => _flights.Any(flight => flight is not null);

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

    public bool GoToSelection(int view) => InShip(view) ? SetShipPilot(view, ShipPilot.Approach) : GoToSelection(view, FlightDuration, null);

    internal bool GoToSelection(int view, double duration, double? range, WorldSnapshot? evaluated = null)
    {
        if (!double.IsFinite(duration) || duration is < 0 or > 300 || (range.HasValue && !double.IsFinite(range.Value)))
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out _, evaluated))
            return false;
        ObserverView observer = _views[view];
        NavigationCamera start = observer.Navigation ?? PresetCamera(observer, snapshot);
        if (start.Placement is not null)
            start = start with { Placement = start.ResolvePlacement(snapshot, DeltaT) };
        double targetRange = Math.Clamp(range ?? FramingRange(target), MinimumRange(target), NavigationCamera.MaxRange);
        _flights[view] = new(start, 0, Math.Max(duration, .001), targetRange);
        _views[view] = observer with { Navigation = start with { Target = target, Tracking = false } };
        return true;
    }

    public bool ToggleTracking(int view) => ToggleTracking(view, null);

    internal bool ToggleTracking(int view, WorldSnapshot? evaluated)
    {
        if (InShip(view))
            return SetShipPilot(view, ShipPilot.Orbit);
        ObserverView observer = _views[view];
        if (observer.Navigation is { Tracking: true } tracked && tracked.Target == observer.Selection)
        {
            NavigationPlacement? placement = tracked.Placement is { UsesFrame: true }
                ? tracked.ResolvePlacement(World.Graph.Evaluate(observer.Epoch), DeltaT) : tracked.Placement;
            _views[view] = observer with { Navigation = tracked with { Tracking = false, Placement = placement } };
            return true;
        }
        if (!TrySelectedState(view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state, evaluated))
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

    public bool HasActiveAutoOrbit => _views.Any(view => view.AutoOrbit is { Enabled: true });

    public bool ToggleAutoOrbit(int view)
    {
        var observer = _views[view];
        var orbit = observer.AutoOrbit ?? CameraAutoOrbit.Default;
        if (!orbit.Enabled && (InShip(view) || observer.Mode == CameraMode.Surface && observer.Navigation is null ||
            (observer.Navigation?.Target ?? observer.Selection) is null))
            return false;
        _views[view] = observer with { AutoOrbit = orbit with { Enabled = !orbit.Enabled } };
        return true;
    }

    public void UpdateAutoOrbit(int view, WorldSnapshot snapshot, double elapsedSeconds)
    {
        var observer = _views[view];
        if (!(elapsedSeconds > 0) || observer.AutoOrbit is not { Enabled: true } orbit ||
            InShip(view) || IsFlying(view) || observer.Mode == CameraMode.Surface && observer.Navigation is null)
            return;
        double radians = (orbit.Clockwise ? -1 : 1) * Math.Tau * Math.Min(elapsedSeconds, .1) / orbit.PeriodSeconds;
        if (observer.Navigation is { Target: { } target })
        {
            if (World.TryNavigationState(target, snapshot, out _, DeltaT))
                AdjustCamera(view, radians, 0, 1);
        }
        else
            TurnScriptView(view, new(0, 1, 0), radians, orbit: true, snapshot);
    }

    public bool CancelFlight(int view)
    {
        if (_flights[view] is null)
            return false;
        _flights[view] = null;
        return true;
    }

    public bool AdjustCamera(int view, double azimuth, double elevation, double zoom, bool orbitTarget = true)
    {
        if (!double.IsFinite(azimuth) || !double.IsFinite(elevation) || !double.IsFinite(zoom) || zoom <= 0)
            return false;
        if (AdjustShipCamera(view, azimuth, elevation, zoom))
            return true;
        ObserverView observer = _views[view];
        if (observer.Navigation is not { } camera)
            return false;
        bool localSurface = camera is { Placement.BodyFixed: true, Target: { } local } &&
            DemoWorlds.Find(local)?.Surface is DemoWorldSurface.Temperate or DemoWorldSurface.Barren or
                DemoWorldSurface.Volcanic or DemoWorldSurface.Desert or DemoWorldSurface.Ice;
        double minimum = camera.Target is { } target && !localSurface ? MinimumRange(target) : NavigationCamera.MinRange;
        CancelFlight(view);
        NavigationCamera next = camera.Adjust(azimuth, elevation, zoom, minimum);
        var placement = camera.Placement;
        bool rotating = azimuth != 0 || elevation != 0;
        if (placement is null && rotating && camera.Target.HasValue)
            placement = camera.ResolveOrbitalPlacement();
        if (placement is { } attitude)
        {
            var turned = rotating ? attitude.Turn(azimuth, elevation, orbit: orbitTarget && camera.Target.HasValue) : attitude;
            next = next with { Placement = turned with
                { OffsetKilometers = turned.OffsetKilometers * (next.RangeKilometers / camera.RangeKilometers) } };
        }
        next = ClampProceduralClearance(next);
        _views[view] = observer with { Navigation = next };
        return true;
    }

    /// <summary>Updates the view pose against the exact world snapshot used by its renderer.</summary>
    public void UpdateCamera(int view, WorldSnapshot snapshot, double elapsedSeconds)
    {
        if (_flights[view] is { ScriptDestination: { } destination } scripted)
        {
            UpdateScriptFlight(view, scripted, destination, snapshot, elapsedSeconds);
            return;
        }
        ObserverView observer = _views[view];
        if (observer.Navigation is not { Target: { } target } pose)
            return;
        // Restoring a saved tracked view keeps its inertial anchor at the saved epoch.
        // Reevaluate the anchor when the clock advances, not on a render refresh or a reload.
        if (_flights[view] is null && (!pose.Tracking || _trackingEpochs[view] == (target, snapshot.Epoch)))
            return;
        Vector3d position;
        if (pose.Placement?.CatalogueFrame is { } catalogue)
        {
            if (!NavigationCamera.TryCatalogueReference(snapshot, target, catalogue, out position, out _))
            {
                CancelFlight(view);
                if (_trackingEpochs[view] is { } last)
                    _views[view] = observer with { Navigation = pose with
                    {
                        Target = null, Tracking = false,
                        Placement = pose.ResolvePlacement(World.Graph.Evaluate(last.Epoch), DeltaT)
                    } };
                _trackingEpochs[view] = null;
                return;
            }
        }
        else if (World.TryNavigationState(target, snapshot, out var state, DeltaT))
        {
            position = state.PositionKilometers;
        }
        else
        {
            CancelFlight(view);
            return;
        }
        if (pose.Placement is { UsesFrame: true } && !pose.TryFrameBasis(snapshot, DeltaT, out _) &&
            _trackingEpochs[view] is { } previous)
            pose = pose with { Placement = pose.ResolvePlacement(World.Graph.Evaluate(previous.Epoch), DeltaT) };
        if (_flights[view] is { } flight)
        {
            double elapsed = Math.Min(flight.Duration, flight.Elapsed + elapsedSeconds);
            double t = elapsed / flight.Duration;
            if (flight.CenteringEnd is { } targetAttitude)
            {
                NavigationPlacement start = flight.Start.Placement!.Value;
                NavigationPlacement current = t == 1 ? targetAttitude : start.Interpolate(targetAttitude, Smooth(t));
                var placement = start with { Forward = current.Forward, Up = current.Up };
                // A temporarily missing local frame has already been frozen in inertial axes above.
                bool lostFrame = start.UsesFrame && pose.Placement is not { UsesFrame: true };
                pose = pose with { AnchorKilometers = position, Placement = lostFrame ? pose.Placement : placement };
                _trackingEpochs[view] = (target, snapshot.Epoch);
                _flights[view] = lostFrame || t == 1 ? null : flight with { Elapsed = elapsed };
                _views[view] = observer with { Navigation = pose };
                return;
            }
            double pan = Smooth(Math.Min(1, t * 1.8));
            double destinationRange = flight.TargetRange;
            double range = InterpolateRange(flight.Start.RangeKilometers, destinationRange, Smooth(t));
            double travel = (position - flight.Start.AnchorKilometers).Length;
            double wide = Math.Min(NavigationCamera.MaxRange, travel * .35);
            if (wide > Math.Max(flight.Start.RangeKilometers, destinationRange) * 2)
            {
                const double pullbackEnd = .3, approachStart = .7;
                pan = Smooth(Math.Clamp((t - pullbackEnd) / (approachStart - pullbackEnd), 0, 1));
                if (t < pullbackEnd)
                    range = InterpolateRange(flight.Start.RangeKilometers, wide, Smooth(t / pullbackEnd));
                else if (t > approachStart)
                    range = InterpolateRange(wide, destinationRange, Smooth((t - approachStart) / (1 - approachStart)));
                else
                    range = wide;
            }
            SurfaceSite? site = World.FindSurfaceSite(target);
            pose = pose with
            {
                AnchorKilometers = pan == 1 ? position : flight.Start.AnchorKilometers + (position - flight.Start.AnchorKilometers) * pan,
                RangeKilometers = t == 1 ? destinationRange : range,
                Placement = null
            };
            if (site is not null)
                pose = pose with { Placement = site.Approach(range), Tracking = true };
            if (t < 1 && (flight.Start.Placement is not null || site is not null))
            {
                var start = flight.Start.ResolvePlacement(snapshot, DeltaT);
                var end = pose.ResolvePlacement(snapshot, DeltaT);
                double blend = Smooth(t);
                var scaledStart = start with { OffsetKilometers = start.OffsetKilometers * (range / flight.Start.RangeKilometers) };
                pose = pose with { Placement = scaledStart.Interpolate(end, blend), Tracking = false };
            }
            if (t == 1 && site is not null)
                _trackingEpochs[view] = (target, snapshot.Epoch);
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
        (target == PlanetCatalogue.SaturnId ? 6.4 : World.BlackHoles.Find(target) is not null ? 60 : 4.2) *
        World.TargetRadiusKilometers(target);

    private bool TrySelectedState(int view, out ObjectId target, out WorldSnapshot snapshot, out CartesianState state,
        WorldSnapshot? evaluated = null)
    {
        ObserverView observer = _views[view];
        snapshot = evaluated?.Epoch == observer.Epoch ? evaluated : World.Graph.Evaluate(observer.Epoch);
        target = observer.Selection.GetValueOrDefault();
        state = default;
        if (observer.Selection is null)
            return false;
        if (World.TryNavigationState(target, snapshot, out state, DeltaT))
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
