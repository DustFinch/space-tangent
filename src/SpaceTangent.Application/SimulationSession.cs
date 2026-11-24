// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public enum ViewSplit { SideBySide, Stacked }

public enum CameraMode { Solar, Lunar, Satellites, Surface, Mission, Timeline, Probe, LunarTerrain, SolarOuter, MarsTerrain }

public readonly record struct LunarTerrainCamera(double LongitudeDegrees, double LatitudeDegrees, double FieldKilometers)
{
    public static LunarTerrainCamera Globe { get; } = new(-20.08, 9.62, 4200);
    public static LunarTerrainCamera Copernicus { get; } = new(-20.08, 9.62, 420);

    public bool IsValid() => LongitudeDegrees is >= -180 and <= 180 && LatitudeDegrees is >= -90 and <= 90 &&
        FieldKilometers is >= 50 and <= 4600;
}

public readonly record struct ObserverView(JulianInstant Epoch, CameraMode Mode, ObjectId? Selection, bool Playing)
{
    public LunarTerrainCamera? Terrain { get; init; }
    public NavigationCamera? Navigation { get; init; }
    public ObjectId? ScriptAimTarget { get; init; }
    public EarthObserverCamera? EarthObserver { get; init; }
    public ObserverPlaceMemory Places { get; init; }
    public EarthObserverCamera ObserverCamera => EarthObserver ?? Places.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
    public EarthObserverCamera CameraForBody(ObjectId body) => ObserverCamera.SurfaceBody == body ? ObserverCamera :
        Places.Find(body) ?? (body == SolarSystemSample.EarthId ? EarthObserverCamera.Default :
            new EarthObserverCamera(new(0, 0, 0), 180, 10) { Body = body });
    public ObserverView WithObserverCamera(EarthObserverCamera camera) => this with
        { EarthObserver = camera, Places = Places.Remember(ObserverCamera).Remember(camera) };
    public SkyLayerSettings Sky { get; init; }
    public BodyReferenceMarks ReferenceMarks { get; init; }
    public double? FieldOfViewDegrees { get; init; }
    public CameraAutoOrbit? AutoOrbit { get; init; }
    public double? MouseSensitivity { get; init; }
    public bool CameraInertia { get; init; }
    public static bool ValidMouseSensitivity(double value) => value is >= .25 and <= 4;
    public double? PlaybackRate { get; init; }
    public bool LightTimeDelayed { get; init; }
    /// <summary>Applied clock displacement, retained across splitting, saving and reloading. Null for older scenes.</summary>
    public long? LightTimeOffsetNanoseconds { get; init; }
    public ObserverOptics? Optics { get; init; }
    public bool SupportsOptics => Mode == CameraMode.Surface || Navigation is not null;
    public VisualOptics? ActiveOptics => SupportsOptics && Optics is { Active: true } optical ? optical.Profile : null;
    public static bool ValidPlaybackRate(double rate) => rate is >= -100_000_000 and <= 100_000_000;
    public double EffectiveFieldOfView => FieldOfViewDegrees ?? (Mode == CameraMode.Surface && Navigation is null ? 10 : 49);
    public static bool ValidFieldOfView(double field) => field is >= 2 and <= 120;
}

public sealed record HuygensMission(SscHuygensTrack Track, SpkKernel SaturnSystem, SpicePolynomialBodyFrame TitanFrame);

public sealed partial class SimulationWorld
{
    public SimulationWorld(ISolarSystemEphemeris ephemeris, IEnumerable<ScienceSatellite> satellites,
        SpiceSpacecraft? mission = null, SscSampledSpacecraft? timeline = null, HuygensMission? probe = null,
        SpicePolynomialBodyFrame? moonFrame = null, BrightStarCatalogue? brightStars = null, PlanetEphemerides? planets = null, bool hasMarsTerrain = false, StellarCatalogue? stellar = null, IEnumerable<SscBodyDefinition>? bodyCatalog = null,
        IEnumerable<SscSurfaceLocation>? locations = null, DeepSkyIndex? deepSky = null, ExoplanetCatalogue? exoplanets = null, IEnumerable<SampledMission>? sampledMissions = null, IEnumerable<LocalTleSatellite>? localSatellites = null, EarthPlaceCatalogue? earthPlaces = null, DistantStarIndex? distantStars = null)
    {
        Ephemeris = ephemeris;
        Planets = planets;
        var sourceWarnings = new List<string>();
        SourceWarnings = sourceWarnings.AsReadOnly();
        Satellites = Array.AsReadOnly(UniqueSources(satellites, item => item.Id,
            item => item.MeshPath, sourceWarnings));
        BrightStars = brightStars ?? BrightStarCatalogue.Empty;
        HasMarsTerrain = hasMarsTerrain;
        Stellar = stellar ?? StellarCatalogue.Empty;
        DeepSky = deepSky ?? DeepSkyIndex.Default;
        Exoplanets = (exoplanets ?? ExoplanetCatalogue.Empty).WithStellarHosts(Stellar);
        StellarOrigin = ephemeris.Sample(JulianInstant.FromJulianDate(2451545, TimeScale.Tdb)).Sun.PositionKilometers;
        var graph = new WorldGraphBuilder();
        graph.AddSolarSystem(ephemeris);
        DemoWorlds.AddTo(graph);
        AddCompactObjects(graph);
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
            PlanetEphemerides.AddPlanet(graph, planet, planets);
        var bodyDefinitions = (bodyCatalog ?? []).ToArray();
        var replacedMinorPlanets = bodyDefinitions.Select(InstalledSscBody.FindBuiltInMinorPlanet)
            .OfType<NaturalBodyDefinition>().Select(body => body.Id).ToHashSet();
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
            if (!replacedMinorPlanets.Contains(body.Id))
                PlanetEphemerides.AddNaturalBody(graph, body, planets);
        _localSatellites = UniqueSources(localSatellites ?? [], item => item.Id,
            item => "TLE " + item.Name, sourceWarnings).ToDictionary(item => item.Id);
        var scienceIds = Satellites.Select(satellite => satellite.Id).ToHashSet();
        foreach (LocalTleSatellite local in _localSatellites.Values)
            if (!scienceIds.Contains(local.Id))
                graph.AddLocalTleSatellite(local);
        foreach (ScienceSatellite satellite in Satellites)
            graph.AddScienceSatellite(satellite, _localSatellites.GetValueOrDefault(satellite.Id));
        if (mission is not null)
        {
            MissionId = graph.AddSpiceSpacecraft(mission);
            MissionName = mission.Name;
        }
        if (timeline is not null)
        {
            graph.AddSampledSpacecraft(timeline);
            TimelineId = timeline.Id;
            TimelineStart = timeline.Start;
        }
        if (probe is not null)
        {
            graph.AddSpkBody(probe.SaturnSystem, 699, 6);
            graph.AddSpkBody(probe.SaturnSystem, 606, 6, probe.TitanFrame);
            graph.AddHuygens(probe.Track);
            Probe = probe.Track;
        }
        if (moonFrame is not null)
        {
            if (moonFrame.BodyId != 301)
                throw new ArgumentException("The lunar terrain requires a Moon body frame.", nameof(moonFrame));
            graph.AddOrientation(SolarSystemSample.MoonId, epoch => moonFrame.Sample(epoch));
            HasLunarTerrain = true;
        }
        else
            PlanetEphemerides.AddMoonFrame(graph, planets);
        SscBodies = InstalledSscBody.Install(graph, bodyDefinitions, Stellar, Exoplanets, BrightStars, StellarOrigin, planets);
        _sscById = SscBodies.ToDictionary(body => body.Id);
        _sampledMissions = UniqueSources(sampledMissions ?? [], item => item.Id,
            item => item.MeshPath ?? item.Name, sourceWarnings).ToDictionary(item => item.Id);
        var reserved = PlanetCatalogue.Planets.SelectMany(body => new[] { body.Id, body.SystemId })
            .Concat(NaturalBodyCatalogue.Bodies.Select(body => body.Id))
            .Concat(DemoWorlds.Bodies.Select(body => body.Id))
            .Concat(BlackHoleCatalogue.Default.Entries.Select(body => body.Id))
            .Concat([SolarSystemSample.SunId, SolarSystemSample.BarycenterId, SolarSystemSample.MoonId, CygnusCompanionId])
            .Concat(_sscById.Keys).Concat(scienceIds).Concat(_localSatellites.Keys).ToHashSet();
        if (MissionId is { } missionId) reserved.Add(missionId);
        if (TimelineId is { } timelineId) reserved.Add(timelineId);
        if (Probe is { } probeMission) reserved.Add(probeMission.Id);
        foreach (var imported in _sampledMissions.Values)
        {
            if (reserved.Contains(imported.Id))
                throw new InvalidDataException($"Mission {imported.Name} ({imported.MeshPath ?? "catalogue"}) conflicts with an existing body {imported.Id.Value}. Disable one of the conflicting sources.");
            graph.AddSampledMission(imported);
        }
        EarthPlaces = earthPlaces ?? EarthPlaceCatalogue.Empty;
        DistantStars = distantStars;
        Catalogue = new(Satellites,  BrightStars, Stellar, DeepSky, Exoplanets, SscBodies, locations ?? [], _sampledMissions.Values, _localSatellites, EarthPlaces, distantStars);
        _sitesById = Catalogue.SurfaceSites.ToDictionary(site => site.Id);
        _sitesByBody = Catalogue.SurfaceSites.GroupBy(site => site.Body)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(site => site.Definition.Importance ?? site.Definition.SizeKilometers).ToArray());
        Graph = graph.Build();
        // Keep pure SSC rules and all their safe dependencies, without running a Lua factory
        // or anything transitively depending on one during a background observation query.
        AlmanacGraph = Graph.WithoutObjects(SscBodies.Where(body => body.Definition.UsesScriptedModels).Select(body => body.Id));
    }

    public ISolarSystemEphemeris Ephemeris { get; }
    public PlanetEphemerides? Planets { get; }
    public WorldGraph AlmanacGraph { get; }
    internal bool HasSurfaceOrientation(ObjectId id) => !_sscById.TryGetValue(id, out var body) ||
        body.Definition.HasExplicitOrientation || body.Definition.BindTimeline is null &&
        (body.Definition.SampleRotation is not null || body.Definition.Orientation is not null);
    public IReadOnlyList<string> SourceWarnings { get; }

    private static T[] UniqueSources<T>(IEnumerable<T> sources, Func<T, ObjectId> id,
        Func<T, string> source, List<string> warnings)
    {
        var kept = new Dictionary<ObjectId, T>();
        foreach (T item in sources)
            if (!kept.TryAdd(id(item), item))
                warnings.Add($"Duplicate {id(item).Value}: kept {source(kept[id(item)])}; ignored {source(item)}.");
        return kept.Values.ToArray();
    }
    public IReadOnlyList<ScienceSatellite> Satellites { get; }
    private readonly Dictionary<ObjectId, InstalledSscBody> _sscById;
    private readonly Dictionary<ObjectId, SampledMission> _sampledMissions;
    private readonly Dictionary<ObjectId, LocalTleSatellite> _localSatellites;
    public IReadOnlyCollection<LocalTleSatellite> LocalSatellites => _localSatellites.Values;
    public LocalTleSatellite? FindLocalSatellite(ObjectId id) => _localSatellites.GetValueOrDefault(id);
    public SampledMission? FindSampledMission(ObjectId id) => _sampledMissions.GetValueOrDefault(id);
    public IReadOnlyList<InstalledSscBody> SscBodies { get; }
    public ObjectCatalogue Catalogue { get; }
    public BrightStarCatalogue BrightStars { get; }
    public StellarCatalogue Stellar { get; }
    public DistantStarIndex? DistantStars { get; }
    public DeepSkyIndex DeepSky { get; }
    public EarthPlaceCatalogue EarthPlaces { get; }
    public ExoplanetCatalogue Exoplanets { get; }
    public Vector3d StellarOrigin { get; }

    private readonly Dictionary<ObjectId, SurfaceSite> _sitesById;
    private readonly Dictionary<ObjectId, SurfaceSite[]> _sitesByBody;

    public SurfaceSite? FindSurfaceSite(ObjectId id) => _sitesById.GetValueOrDefault(id);
    public IReadOnlyList<SurfaceSite> SurfaceSitesOn(ObjectId id) => _sitesByBody.GetValueOrDefault(id) ?? [];

    public bool TrySurfaceFrame(SurfaceSite site, WorldSnapshot snapshot, DeltaTModel model,
        out CartesianState body, out Basis3d orientation)
    {
        orientation = default;
        return snapshot.TryState(site.Body, out body) && NavigationCamera.TryBodyBasis(snapshot, site.Body, model, out orientation);
    }

    /// <summary>The state of any navigable target. Earth surface sites depend on the observer's ΔT model.</summary>
    public bool TryNavigationState(ObjectId id, WorldSnapshot snapshot, out CartesianState state, DeltaTModel deltaT)
    {
        if (snapshot.TryState(id, out state))
            return true;
        if (FindSurfaceSite(id) is { } site && TrySurfaceFrame(site, snapshot, deltaT, out CartesianState body, out Basis3d frame))
        {
            Vector3d offset = frame.Transform(site.OffsetKilometers);
            Vector3d spin = snapshot.TryOrientation(site.Body, out var rotation)
                ? rotation.AngularVelocityRadiansPerSecond.GetValueOrDefault()
                : site.Body == SolarSystemSample.EarthId
                    ? SpaceTangent.Core.Coordinates.EarthOrientation.TerrestrialToWorld(snapshot.Epoch, deltaT)
                        .AngularVelocityRadiansPerSecond.GetValueOrDefault() : default;
            state = body + new CartesianState(offset, Vector3d.Cross(spin, offset));
            return true;
        }
        if (Exoplanets.Find(id) is { } planet && FindStellarTarget(planet.Host) is { } host)
        {
            CartesianState relative = planet.ParentRelativeState(snapshot.Epoch);
            for (int depth = 0; planet.Parent != planet.Host; depth++)
            {
                if (depth == 4 || Exoplanets.Find(planet.Parent) is not { } parent)
                    return false;
                relative += parent.ParentRelativeState(snapshot.Epoch);
                planet = parent;
            }
            state = PlaceStar(host, snapshot.Epoch) + relative;
            return true;
        }
        if (FindStellarTarget(id) is { } star)
        {
            state = PlaceStar(star, snapshot.Epoch);
            return true;
        }
        if (DeepSky.Find(id) is { } deepSky)
        {
            state = new(deepSky.PositionKilometers + StellarOrigin, default);
            return true;
        }
        return false;
    }

    public StellarEntry? FindStellarTarget(ObjectId id) => Stellar.Find(Exoplanets.CanonicalHost(id)) ?? Exoplanets.FindHost(id) ?? DistantStars?.Find(id);

    private CartesianState PlaceStar(StellarEntry star, JulianInstant epoch)
    {
        CartesianState relative = star.StateAt(epoch);
        return new(relative.PositionKilometers + StellarOrigin, relative.VelocityKilometersPerSecond);
    }

    public double TargetRadiusKilometers(ObjectId id) =>
        FindSurfaceSite(id)?.ScaleKilometers ?? StarRadius(FindStellarTarget(id)) ?? DeepSky.Find(id)?.RadiusKilometers ??
        Exoplanets.Find(id)?.RadiusKilometers ?? DemoWorlds.Find(id)?.RadiusKilometers ??
        _sscById.GetValueOrDefault(id)?.Definition.RadiusKilometers ??
        MissionNavigationRadius(id) ?? BlackHoles.Find(id)?.SchwarzschildRadiusKilometers ??
        (id == CygnusCompanionId ? CygnusCompanionRadiusKilometers : NavigationCamera.BodyRadiusKilometers(id));
    private double? MissionNavigationRadius(ObjectId id) => _sampledMissions.TryGetValue(id, out var mission)
        ? mission.NavigationRadiusKilometers ?? mission.RadiusKilometers : null;

    private static double? StarRadius(StellarEntry? star) => star is null ? null :
        star.IsBarycenter ? Math.Max(1000, star.SystemRadiusKilometers) : Math.Max(.007, star.RadiusKilometers);

    public WorldGraph Graph { get; }
    public ObjectId? MissionId { get; }
    public string? MissionName { get; }
    public ObjectId? TimelineId { get; }
    public JulianInstant? TimelineStart { get; }
    public SscHuygensTrack? Probe { get; }
    public bool HasLunarTerrain { get; }
    public bool HasMarsTerrain { get; }
}

/// <summary>Owns clocks and view preferences independently of rendering resources.</summary>
public sealed partial class SimulationSession
{
    // A recorded Cassini Saturn orbit insertion epoch with both state and pointing coverage.
    public static JulianInstant MissionPreview { get; } = JulianInstant.FromTdbSeconds(141917558.4203589);
    private readonly List<ObserverView> _views;
    /// <summary>View indices whose saved non-Earth site was reset by the last successful world replacement.</summary>
    public IReadOnlyList<int> SurfaceObserverFallbacks { get; private set; } = [];
    private readonly ObserverClock _clocks;
    private readonly List<int> _viewIds = [0];
    private int _nextViewId = 1;
    private readonly Dictionary<int, bool> _scriptedZoomChanges = [];
    public int ObserverPlacesVersion { get; private set; }

    public SimulationSession(SimulationWorld world, JulianInstant initialEpoch, DeltaTModel deltaT)
    {
        if (!Covers(world, initialEpoch))
            throw new ArgumentOutOfRangeException(nameof(initialEpoch));
        World = world;
        _views = [new(initialEpoch, CameraMode.Solar, null, false) { Places = default(ObserverPlaceMemory).EnsureIdentity() }];
        _clocks = new(_views);
        SetDeltaT(deltaT);
    }

    public SimulationWorld World { get; private set; }
    public DeltaTModel DeltaT { get; private set; }
    public bool Synchronized => _clocks.Synchronized;
    public bool NightMode { get; set; }
    public ViewSplit SplitLayout { get; private set; }
    public ObserverLayout Layout { get; private set; } = ObserverLayout.Leaf(0);
    public int ViewCount => _views.Count;
    public ObserverView this[int view] => _views[view];

    public int ViewIdentity(int view) => _viewIds[view];
    internal int IndexOfView(int identity) => _viewIds.IndexOf(identity);

    public SimulationScenario CaptureScenario(int activeView)
    {
        return new(Array.AsReadOnly(_views.ToArray()), Synchronized, _clocks.Owner, activeView, DeltaT)
            { SplitLayout = SplitLayout, Layout = Layout.IsDefault ? null : Layout, NightMode = NightMode };
    }

    public void RestoreScenario(SimulationScenario scenario)
    {
        ObserverView[] views = scenario.Views.ToArray();
        if (views.Where(view => view.Places.HasIdentity).Select(view => view.Places.ViewKey).Distinct().Count() !=
            views.Count(view => view.Places.HasIdentity))
            throw new InvalidDataException("The scenario has duplicate observing place identities.");
        if (views.Length is < 1 or > ObserverLayout.MaximumViews || scenario.ClockOwner < 0 || scenario.ClockOwner >= views.Length ||
            scenario.ActiveView < 0 || scenario.ActiveView >= views.Length)
            throw new InvalidDataException("The scenario refers to an unavailable view.");
        if (!Enum.IsDefined(scenario.DeltaT))
            throw new InvalidDataException("The scenario has an unknown ΔT model.");
        ObserverLayout layout = ObserverLayout.Restore(scenario.Layout, views.Length, scenario.SplitLayout);
        foreach (ObserverView view in views)
        {
            if (!Covers(World, view.Epoch))
                throw new InvalidDataException("The scenario epoch is outside the current ephemeris.");
            if (!Enum.IsDefined(view.Mode))
                throw new InvalidDataException("The scenario has an unknown camera mode.");
            if (view.Mode == CameraMode.Mission && World.MissionId is null)
                throw new InvalidDataException("The scenario needs the Cassini mission add-on.");
            if (view.Mode == CameraMode.Timeline && World.TimelineId is null)
                throw new InvalidDataException("The scenario needs the Cassini timeline catalogue.");
            if (view.Mode == CameraMode.Probe && World.Probe is null)
                throw new InvalidDataException("The scenario needs the Huygens timeline and Titan kernels.");
            if (view.Mode == CameraMode.LunarTerrain && !World.HasLunarTerrain)
                throw new InvalidDataException("The scenario needs the lunar elevation map and orientation kernel.");
            if (view.Mode == CameraMode.MarsTerrain && !World.HasMarsTerrain)
                throw new InvalidDataException("The scenario needs the Martian elevation map and orientation kernel.");
            if (view.EarthObserver is { } earth && !earth.IsValid())
                throw new InvalidDataException("The scenario has an invalid Earth observer camera.");
            if (!view.Places.IsValid())
                throw new InvalidDataException("The scenario has an invalid observing place memory.");
            if (view.EarthObserver is { IsEarth: false } surface &&
                !SurfaceObserver.TryFrame(World, surface, World.Graph.Evaluate(view.Epoch, scenario.DeltaT), scenario.DeltaT, out _))
                throw new InvalidDataException("The scenario needs the observing body's position and orientation at its saved epoch.");
            if (view.FieldOfViewDegrees is { } field && !ObserverView.ValidFieldOfView(field))
                throw new InvalidDataException("The scenario has an invalid field of view.");
            if (view.Optics is { } optical && !optical.IsValid())
                throw new InvalidDataException("The scenario has an invalid optics profile.");
            if (view.PlaybackRate is { } rate && !ObserverView.ValidPlaybackRate(rate))
                throw new InvalidDataException("The scenario has an invalid playback rate.");
            if (!view.Sky.IsValid() || view.AutoOrbit is { } autoOrbit && !autoOrbit.IsValid() ||
                view.MouseSensitivity is { } sensitivity && !ObserverView.ValidMouseSensitivity(sensitivity))
                throw new InvalidDataException("The scenario has invalid sky or camera settings.");
            if (view.LightTimeOffsetNanoseconds is { } offset && (offset < 0 || !view.LightTimeDelayed))
                throw new InvalidDataException("The scenario has an invalid light-time offset.");
            if ((view.ReferenceMarks & ~BodyReferenceMarks.All) != 0)
                throw new InvalidDataException("The scenario has unsupported reference marks.");
            if (view.Terrain is { } terrain && !terrain.IsValid())
                throw new InvalidDataException("The scenario has invalid lunar camera coordinates or field width.");
            if (view.Mode == CameraMode.Satellites && World.Satellites.Count == 0)
                throw new InvalidDataException("The scenario needs the Earth satellite add-ons.");
            if (view.Selection is { } selection && World.Catalogue.Find(selection) is null)
                throw new InvalidDataException($"The selected object {selection.Value} is missing from the current world.");
            if (view.ScriptAimTarget is { } aim && World.Catalogue.Find(aim) is null)
                throw new InvalidDataException("The scenario has an unavailable script aim target.");
            if (view.Navigation is { } camera &&
                (!camera.IsValid() || (camera.Target is { } target && World.Catalogue.Find(target) is null)))
                throw new InvalidDataException("The scenario has an invalid camera pose or unavailable camera target.");
            if (view.Navigation is { Placement.UsesFrame: true } framed &&
                !framed.TryFrameBasis(World.Graph.Evaluate(view.Epoch, scenario.DeltaT), scenario.DeltaT, out _))
                throw new InvalidDataException("The scene needs an unavailable observer frame.");
        }
        ObserverView owner = views[scenario.ClockOwner];
        if (scenario.Synchronized && views.Any(view => view.Epoch != owner.Epoch || view.Playing != owner.Playing))
            throw new InvalidDataException("Synchronized scenario clocks must agree.");

        _ships.Clear();
        _views.Clear();
        _views.AddRange(views.Select(view => view with { Places = view.Places.EnsureIdentity() }));
        _flights.Clear();
        _trackingEpochs.Clear();
        _scriptNotices.Clear();
        _scriptMarkers.Clear();
        _viewIds.Clear();
        _scriptedZoomChanges.Clear();
        foreach (ObserverView view in views)
        {
            _flights.Add(null);
            _trackingEpochs.Add(view.Navigation is { Tracking: true, Target: { } target } ? (target, view.Epoch) : null);
            _viewIds.Add(_nextViewId++);
        }
        Layout = layout;
        _clocks.Restore(scenario.Synchronized, scenario.ClockOwner);
        NightMode = scenario.NightMode;
        if (Synchronized)
            SharePlaybackRate(owner.PlaybackRate ?? DefaultPlaybackRate(owner.Mode));
        SplitLayout = scenario.SplitLayout;
        DeltaT = scenario.DeltaT;
        ObserverPlacesVersion++;
    }

    public void SetEarthObserver(int view, EarthObserverCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view].WithObserverCamera(camera);
        ObserverPlacesVersion++;
    }

    public bool SetObserverBody(int view, ObjectId body)
    {
        var observer = _views[view];
        var camera = observer.CameraForBody(body);
        if (!SurfaceObserver.TryFrame(World, camera, World.Graph.Evaluate(observer.Epoch, DeltaT), DeltaT, out _)) return false;
        SetEarthObserver(view, camera);
        return true;
    }

    internal void RestoreObserverPlaces(int view, EarthObserverCamera camera, ObserverPlaceMemory memory)
    {
        if (!camera.IsValid() || !memory.HasIdentity || !memory.IsValid())
            throw new InvalidDataException("Invalid saved observing places.");
        var snapshot = World.Graph.Evaluate(_views[view].Epoch, DeltaT);
        if (!SurfaceObserver.TryFrame(World, camera, snapshot, DeltaT, out _))
        {
            memory = memory.Remember(camera);
            camera = memory.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
        }
        _views[view] = _views[view] with { EarthObserver = camera, Places = memory };
        ObserverPlacesVersion++;
    }

    public void SetOptics(int view, ObserverOptics optics)
    {
        if (!optics.IsValid())
            throw new ArgumentOutOfRangeException(nameof(optics));
        _views[view] = _views[view] with { Optics = optics };
    }

    public void SetSkyLayers(int view, SkyLayerSettings sky) => _views[view] = _views[view] with { Sky = sky };

    public void SetSplitLayout(ViewSplit layout)
    {
        if (!Enum.IsDefined(layout))
            throw new ArgumentOutOfRangeException(nameof(layout));
        SplitLayout = layout;
        if (Layout.View is null)
            Layout = Layout with { Direction = layout };
    }

    public void SetFieldOfView(int view, double? field)
    {
        if (field is { } degrees && !ObserverView.ValidFieldOfView(degrees))
            throw new ArgumentOutOfRangeException(nameof(field));
        _views[view] = _views[view] with { FieldOfViewDegrees = field };
    }

    internal void SetScriptFieldOfView(int view, double degrees, bool synchronizeZoom, bool synchronizeOtherViews)
    {
        SetFieldOfView(view, degrees);
        if (synchronizeZoom && synchronizeOtherViews)
        {
            foreach (int identity in _viewIds)
                _scriptedZoomChanges[identity] = true;
        }
        else if (synchronizeZoom)
            _scriptedZoomChanges[ViewIdentity(view)] = true;
        else
            _scriptedZoomChanges.TryAdd(ViewIdentity(view), false);
    }

    /// <summary>Consumes a script request to keep or reset the view's stored zoom. Null means no request.</summary>
    public bool? TakeScriptedZoomChange(int view) =>
        _scriptedZoomChanges.Remove(ViewIdentity(view), out bool synchronize) ? synchronize : null;

    public void Split()
    {
        if (_views.Count == 1)
            SplitView(0, SplitLayout);
    }

    public bool SplitView(int view, ViewSplit direction) => SplitView(view, direction, view, .5, false);

    public bool SplitView(int view, ViewSplit direction, int copyFrom, double share, bool newFirst)
    {
        if (!double.IsFinite(share) || share is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(share));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (_views.Count == ObserverLayout.MaximumViews)
            return false;
        if (Synchronized)
            SharePlaybackRate(CurrentPlaybackRate(copyFrom));
        ObserverView source = _views[copyFrom];
        Layout = Layout.Insert(view, _views.Count, direction, share, newFirst);
        SplitLayout = Layout.Direction;
        _views.Add(source with { Places = source.Places.ForkIdentity() });
        _flights.Add(null);
        _trackingEpochs.Add(_trackingEpochs[copyFrom]);
        _viewIds.Add(_nextViewId++);
        ObserverPlacesVersion++;
        return true;
    }

    public void ResizeViewDivider(string path, double share)
    {
        if (!double.IsFinite(share) || share is <= 0 or >= 1)
            return;
        Layout = Layout.Resize(path, share);
    }

    public void CloseSecondView()
    {
        if (_views.Count == 2)
            RemoveView(1);
    }

    public void RemoveView(int view)
    {
        if (_views.Count == 1)
            return;
        _views.RemoveAt(view);
        _flights.RemoveAt(view);
        _trackingEpochs.RemoveAt(view);
        _ships.Remove(ViewIdentity(view));
        ClearScriptNotice(view);
        _scriptMarkers.Remove(_viewIds[view]);
        _scriptedZoomChanges.Remove(ViewIdentity(view));
        _viewIds.RemoveAt(view);
        ObserverPlacesVersion++;
        Layout = Layout.Remove(view)!;
        if (Layout.View is null)
            SplitLayout = Layout.Direction;
        _clocks.OnViewRemoved(view);
    }

    public void Synchronize(bool enabled, int source) => _clocks.Synchronize(enabled, source);

    public void SetDeltaT(DeltaTModel model)
    {
        if (model is < DeltaTModel.EspenakMeeus2006 or > DeltaTModel.FixedOffset)
            throw new ArgumentOutOfRangeException(nameof(model));
        DeltaT = model;
    }

    public void SetMode(int view, CameraMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == CameraMode.LunarTerrain && !World.HasLunarTerrain)
            throw new InvalidOperationException("The current world has no lunar terrain.");
        if (mode == CameraMode.MarsTerrain && !World.HasMarsTerrain)
            throw new InvalidOperationException("The current world has no Martian terrain.");
        ObserverView previous = _views[view];
        if (mode == CameraMode.Satellites)
        {
            ScienceSatellite target = World.Satellites.FirstOrDefault(satellite => satellite.Id == previous.Selection)
                ?? World.Satellites.FirstOrDefault()
                ?? throw new InvalidOperationException("The current world has no satellites.");
            Select(view, target.Id);
        }
        if (mode == CameraMode.Mission)
        {
            ObjectId mission = World.MissionId ?? throw new InvalidOperationException("The current world has no mission.");
            WorldSnapshot snapshot = World.Graph.Evaluate(previous.Epoch, DeltaT);
            if (!snapshot.TryState(mission, out _) || !snapshot.TryOrientation(mission, out _))
                Seek(view, MissionPreview);
        }
        if (mode == CameraMode.Timeline)
        {
            ObjectId id = World.TimelineId ?? throw new InvalidOperationException("The current world has no sampled timeline.");
            if (!World.Graph.Evaluate(previous.Epoch, DeltaT).TryState(id, out _))
                Seek(view, World.TimelineStart!.Value);
        }
        if (mode == CameraMode.Probe)
        {
            var probe = World.Probe ?? throw new InvalidOperationException("The current world has no Huygens track.");
            if (!World.Graph.Evaluate(previous.Epoch, DeltaT).TryOrientation(probe.Id, out _))
                Seek(view, probe.Separation);
        }
        CancelFlight(view);
        LeaveShip(view);
        double sharedRate = CurrentPlaybackRate(view);
        _views[view] = _views[view] with { Mode = mode, Navigation = null };
        if (Synchronized && ViewCount > 1)
            SharePlaybackRate(sharedRate);
    }

    public void SetLunarTerrainCamera(int view, LunarTerrainCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view] with { Terrain = camera };
    }

    public void SelectObject(int view, ObjectId selection)
    {
        if (World.Catalogue.Find(selection) is null)
            throw new ArgumentOutOfRangeException(nameof(selection));
        _views[view] = _views[view] with { Selection = selection };
    }

    public void Select(int view, ObjectId selection)
    {
        ObserverView previous = _views[view];
        ScienceSatellite target = World.Satellites.FirstOrDefault(satellite => satellite.Id == selection)
            ?? throw new ArgumentOutOfRangeException(nameof(selection));
        bool needsEpoch = !target.TrySample(previous.Epoch, out _);
        if (needsEpoch && !Covers(World, target.Epoch))
            throw new InvalidOperationException("The selected orbit and the current ephemeris have no common starting epoch.");

        _views[view] = previous with { Selection = selection };
        if (needsEpoch)
        {
            SetClock(view, target.Epoch, previous.Playing);
            LastNavigationEpochChange = new(++_navigationEpochRevision, target.Id, previous.Epoch, target.Epoch, Synchronized);
        }
    }

    public void TogglePlaying(int view) => SetPlaying(view, !_views[view].Playing);

    public void SetPlaying(int view, bool playing) => SetClock(view, _views[view].Epoch, playing);

    // Synchronized views share one clock, so they share its rate. Otherwise the rate would change whenever another view
    // became the clock owner.
    public void SetPlaybackRate(int view, double rate) => SetPlaybackRate(view, rate, synchronizeRate: true);

    internal void SetPlaybackRate(int view, double rate, bool synchronizeRate) =>
        _clocks.SetPlaybackRate(view, rate, synchronizeRate);

    public void Seek(int view, JulianInstant epoch)
    {
        if (!Covers(World, epoch))
            throw new ArgumentOutOfRangeException(nameof(epoch));
        SetClock(view, epoch, _views[view].Playing);
    }

    public void Step(int view, double seconds)
    {
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _clocks.Step(view, seconds, World.Ephemeris.Start, World.Ephemeris.End);
    }

    internal static double DefaultPlaybackRate(CameraMode mode) => ObserverClock.DefaultPlaybackRate(mode);

    public bool Tick(double elapsed)
    {
        bool changed = ExpireScriptNotices(elapsed);
        int start = Synchronized ? _clocks.Owner : 0;
        int end = Synchronized ? start + 1 : _views.Count;
        for (int view = start; view < end; view++)
        {
            if (_views[view].Playing)
            {
                double rate = CurrentPlaybackRate(view);
                Step(view, elapsed * rate);
                changed = true;
            }
        }
        return changed;
    }

    public bool TryReplaceWorld(SimulationWorld next)
    {
        if (_views.Any(view => !Covers(next, view.Epoch)))
            return false;

        var fallbacks = new List<int>();
        for (int index = 0; index < _views.Count; index++)
        {
            ObserverView previous = _views[index];
            ObjectId? selected = previous.Selection is { } id && next.Catalogue.Find(id) is not null
                ? id
                : null;
            bool lostMode = (previous.Mode == CameraMode.Satellites && next.Satellites.Count == 0) ||
                (previous.Mode == CameraMode.Mission && next.MissionId is null) ||
                (previous.Mode == CameraMode.Timeline && next.TimelineId is null) ||
                (previous.Mode == CameraMode.Probe && next.Probe is null) ||
                (previous.Mode == CameraMode.LunarTerrain && !next.HasLunarTerrain) ||
                (previous.Mode == CameraMode.MarsTerrain && !next.HasMarsTerrain);
            CameraMode mode = lostMode
                ? CameraMode.Solar
                : previous.Mode;
            NavigationCamera? navigation = previous.Navigation;
            if (navigation is { Target: { } target } camera)
            {
                bool lostTarget = next.Catalogue.Find(target) is null;
                bool lostFrame = camera.Placement is { UsesFrame: true } &&
                    !camera.TryFrameBasis(next.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT, out _);
                if (lostTarget || lostFrame)
                {
                    NavigationPlacement? placement = camera.Placement is { UsesFrame: true }
                        ? camera.ResolvePlacement(World.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT) : camera.Placement;
                    bool release = lostTarget || camera.Placement?.CatalogueFrame is not null;
                    navigation = camera with { Target = release ? null : target, Tracking = !release && camera.Tracking,
                        Placement = placement };
                    CancelFlight(index);
                }
            }
            ObjectId? aim = previous.ScriptAimTarget is { } tracked && next.Catalogue.Find(tracked) is not null ? tracked : null;
            EarthObserverCamera? site = previous.EarthObserver;
            if (site is { IsEarth: false } surface &&
                !SurfaceObserver.TryFrame(next, surface, next.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT, out _))
            {
                site = previous.Places.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
                if (mode == CameraMode.Surface && navigation is null) mode = CameraMode.Solar;
                fallbacks.Add(index);
            }
            var updated = previous with { Selection = selected, Mode = mode, Navigation = navigation, ScriptAimTarget = aim };
            _views[index] = site is { } location && site != previous.EarthObserver ? updated.WithObserverCamera(location) :
                updated with { EarthObserver = site };
        }
        World = next;
        SurfaceObserverFallbacks = fallbacks.AsReadOnly();
        if (fallbacks.Count > 0) ObserverPlacesVersion++;
        return true;
    }

    private void SetClock(int view, JulianInstant epoch, bool playing) => _clocks.SetClock(view, epoch, playing);

    private void SharePlaybackRate(double rate) => _clocks.SharePlaybackRate(rate);

    private static bool Covers(SimulationWorld world, JulianInstant epoch) =>
        epoch.Scale == TimeScale.Tdb && epoch.CompareTo(world.Ephemeris.Start) >= 0 &&
        epoch.CompareTo(world.Ephemeris.End) <= 0;
}
