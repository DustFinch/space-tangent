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
    public EarthObserverCamera? EarthObserver { get; init; }
    public SkyLayerSettings Sky { get; init; }
    public BodyReferenceMarks ReferenceMarks { get; init; }
    public double? FieldOfViewDegrees { get; init; }
    public double? PlaybackRate { get; init; }
    public static bool ValidPlaybackRate(double rate) => rate is >= -100_000_000 and <= 100_000_000;
    public double EffectiveFieldOfView => FieldOfViewDegrees ?? (Mode == CameraMode.Surface && Navigation is null ? 10 : 49);
    public static bool ValidFieldOfView(double field) => field is >= 2 and <= 120;
}

public sealed record HuygensMission(SscHuygensTrack Track, SpkKernel SaturnSystem, SpicePolynomialBodyFrame TitanFrame);

public sealed class SimulationWorld
{
    public SimulationWorld(ISolarSystemEphemeris ephemeris, IEnumerable<ScienceSatellite> satellites,
        SpiceSpacecraft? mission = null, SscSampledSpacecraft? timeline = null, HuygensMission? probe = null,
        SpicePolynomialBodyFrame? moonFrame = null, BrightStarCatalogue? brightStars = null, PlanetEphemerides? planets = null, bool hasMarsTerrain = false, StellarCatalogue? stellar = null, IEnumerable<SscBodyDefinition>? bodyCatalog = null,
        IEnumerable<SscSurfaceLocation>? locations = null)
    {
        Ephemeris = ephemeris;
        Satellites = Array.AsReadOnly(satellites.ToArray());
        BrightStars = brightStars ?? BrightStarCatalogue.Empty;
        HasMarsTerrain = hasMarsTerrain;
        Stellar = stellar ?? StellarCatalogue.Empty;
        StellarOrigin = ephemeris.Sample(JulianInstant.FromJulianDate(2451545, TimeScale.Tdb)).Sun.PositionKilometers;
        var graph = new WorldGraphBuilder();
        graph.AddSolarSystem(ephemeris);
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
            PlanetEphemerides.AddPlanet(graph, planet, planets);
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
            PlanetEphemerides.AddNaturalBody(graph, body, planets);
        foreach (ScienceSatellite satellite in Satellites)
            graph.AddScienceSatellite(satellite);
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
        SscBodies = InstalledSscBody.Install(graph, bodyCatalog ?? []);
        _sscById = SscBodies.ToDictionary(body => body.Id);
        Catalogue = new(Satellites, BrightStars, Stellar, SscBodies, locations ?? []);
        _sitesById = Catalogue.SurfaceSites.ToDictionary(site => site.Id);
        _sitesByBody = Catalogue.SurfaceSites.GroupBy(site => site.Body)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(site => site.Definition.Importance ?? site.Definition.SizeKilometers).ToArray());
        Graph = graph.Build();
    }

    public ISolarSystemEphemeris Ephemeris { get; }
    public IReadOnlyList<ScienceSatellite> Satellites { get; }
    private readonly Dictionary<ObjectId, InstalledSscBody> _sscById;
    public IReadOnlyList<InstalledSscBody> SscBodies { get; }
    public ObjectCatalogue Catalogue { get; }
    public BrightStarCatalogue BrightStars { get; }
    public StellarCatalogue Stellar { get; }
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

    public bool TryNavigationState(ObjectId id, WorldSnapshot snapshot, out CartesianState state,
        DeltaTModel deltaT = DeltaTModel.MorrisonStephenson2004)
    {
        if (snapshot.TryState(id, out state))
            return true;
        if (FindSurfaceSite(id) is { } site && TrySurfaceFrame(site, snapshot, deltaT, out CartesianState body, out Basis3d frame))
        {
            Vector3d offset = frame.Transform(site.OffsetKilometers);
            Vector3d spin = site.Body == SolarSystemSample.EarthId ? frame.Z * (2 * Math.PI / 86164.0905) :
                snapshot.TryOrientation(site.Body, out var rotation) ? rotation.AngularVelocityRadiansPerSecond.GetValueOrDefault() : default;
            state = body + new CartesianState(offset, Vector3d.Cross(spin, offset));
            return true;
        }
        if (Stellar.Find(id) is { } star)
        {
            CartesianState relative = star.StateAt(snapshot.Epoch);
            state = new(relative.PositionKilometers + StellarOrigin, relative.VelocityKilometersPerSecond);
            return true;
        }
        if (DeepSkyCatalogue.Find(id) is { } deepSky)
        {
            state = new(deepSky.PositionKilometers + StellarOrigin, default);
            return true;
        }
        return false;
    }

    public double TargetRadiusKilometers(ObjectId id) =>
        FindSurfaceSite(id)?.ScaleKilometers ?? Stellar.Find(id)?.RadiusKilometers ?? DeepSkyCatalogue.Find(id)?.RadiusKilometers ??
        _sscById.GetValueOrDefault(id)?.Definition.RadiusKilometers ?? NavigationCamera.BodyRadiusKilometers(id);
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
    private int _clockOwner;
    private readonly List<int> _viewIds = [0];
    private int _nextViewId = 1;

    public SimulationSession(SimulationWorld world, JulianInstant initialEpoch, DeltaTModel deltaT)
    {
        if (!Covers(world, initialEpoch))
            throw new ArgumentOutOfRangeException(nameof(initialEpoch));
        World = world;
        _views = [new(initialEpoch, CameraMode.Solar, null, false)];
        SetDeltaT(deltaT);
    }

    public SimulationWorld World { get; private set; }
    public DeltaTModel DeltaT { get; private set; }
    public bool Synchronized { get; private set; } = true;
    public ViewSplit SplitLayout { get; private set; }
    public ObserverLayout Layout { get; private set; } = ObserverLayout.Leaf(0);
    public int ViewCount => _views.Count;
    public ObserverView this[int view] => _views[view];

    internal int ViewIdentity(int view) => _viewIds[view];
    internal int IndexOfView(int identity) => _viewIds.IndexOf(identity);

    public SimulationScenario CaptureScenario(int activeView)
    {
        return new(Array.AsReadOnly(_views.ToArray()), Synchronized, _clockOwner, activeView, DeltaT)
            { SplitLayout = SplitLayout, Layout = Layout.IsDefault ? null : Layout };
    }

    public void RestoreScenario(SimulationScenario scenario)
    {
        ObserverView[] views = scenario.Views.ToArray();
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
            if (view.FieldOfViewDegrees is { } field && !ObserverView.ValidFieldOfView(field))
                throw new InvalidDataException("The scenario has an invalid field of view.");
            if (view.PlaybackRate is { } rate && !ObserverView.ValidPlaybackRate(rate))
                throw new InvalidDataException("The scenario has an invalid playback rate.");
            if ((view.ReferenceMarks & ~BodyReferenceMarks.All) != 0)
                throw new InvalidDataException("The scenario has unsupported reference marks.");
            if (view.Terrain is { } terrain && !terrain.IsValid())
                throw new InvalidDataException("The scenario has invalid lunar camera coordinates or field width.");
            if (view.Mode == CameraMode.Satellites && World.Satellites.Count == 0)
                throw new InvalidDataException("The scenario needs the Earth satellite add-ons.");
            if (view.Selection is { } selection && World.Catalogue.Find(selection) is null)
                throw new InvalidDataException($"The selected object {selection.Value} is missing from the current world.");
            if (view.Navigation is { } camera &&
                (!camera.IsValid() || (camera.Target is { } target && World.Catalogue.Find(target) is null)))
                throw new InvalidDataException("The scenario has an invalid camera pose or unavailable camera target.");
            if (view.Navigation is { Placement.UsesFrame: true } framed &&
                !framed.TryFrameBasis(World.Graph.Evaluate(view.Epoch), scenario.DeltaT, out _))
                throw new InvalidDataException("The scene needs an unavailable observer frame.");
        }
        ObserverView owner = views[scenario.ClockOwner];
        if (scenario.Synchronized && views.Any(view => view.Epoch != owner.Epoch || view.Playing != owner.Playing))
            throw new InvalidDataException("Synchronized scenario clocks must agree.");

        _ships.Clear();
        _views.Clear();
        _views.AddRange(views);
        _flights.Clear();
        _trackingEpochs.Clear();
        _viewIds.Clear();
        foreach (ObserverView view in views)
        {
            _flights.Add(null);
            _trackingEpochs.Add(view.Navigation is { Tracking: true, Target: { } target } ? (target, view.Epoch) : null);
            _viewIds.Add(_nextViewId++);
        }
        Layout = layout;
        Synchronized = scenario.Synchronized;
        _clockOwner = scenario.ClockOwner;
        SplitLayout = scenario.SplitLayout;
        DeltaT = scenario.DeltaT;
    }

    public void SetEarthObserver(int view, EarthObserverCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view] with { EarthObserver = camera };
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

    public void Split()
    {
        if (_views.Count == 1)
            SplitView(0, SplitLayout);
    }

    public bool SplitView(int view, ViewSplit direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (_views.Count == ObserverLayout.MaximumViews)
            return false;
        ObserverView source = _views[view];
        Layout = Layout.Insert(view, _views.Count, direction);
        SplitLayout = Layout.Direction;
        _views.Add(source);
        _flights.Add(null);
        _trackingEpochs.Add(_trackingEpochs[view]);
        _viewIds.Add(_nextViewId++);
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
        _viewIds.RemoveAt(view);
        Layout = Layout.Remove(view)!;
        if (Layout.View is null)
            SplitLayout = Layout.Direction;
        if (_clockOwner == view)
            _clockOwner = 0;
        else if (_clockOwner > view)
            _clockOwner--;
    }

    public void Synchronize(bool enabled, int source)
    {
        ObserverView anchor = _views[source];
        if (Synchronized == enabled)
            return;
        Synchronized = enabled;
        if (enabled)
            SetClock(source, anchor.Epoch, anchor.Playing);
    }

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
            WorldSnapshot snapshot = World.Graph.Evaluate(previous.Epoch);
            if (!snapshot.TryState(mission, out _) || !snapshot.TryOrientation(mission, out _))
                Seek(view, MissionPreview);
        }
        if (mode == CameraMode.Timeline)
        {
            ObjectId id = World.TimelineId ?? throw new InvalidOperationException("The current world has no sampled timeline.");
            if (!World.Graph.Evaluate(previous.Epoch).TryState(id, out _))
                Seek(view, World.TimelineStart!.Value);
        }
        if (mode == CameraMode.Probe)
        {
            var probe = World.Probe ?? throw new InvalidOperationException("The current world has no Huygens track.");
            if (!World.Graph.Evaluate(previous.Epoch).TryOrientation(probe.Id, out _))
                Seek(view, probe.Separation);
        }
        CancelFlight(view);
        LeaveShip(view);
        _views[view] = _views[view] with { Mode = mode, Navigation = null };
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
            SetClock(view, target.Epoch, previous.Playing);
    }

    public void TogglePlaying(int view) => SetPlaying(view, !_views[view].Playing);

    public void SetPlaying(int view, bool playing) => SetClock(view, _views[view].Epoch, playing);

    public void SetPlaybackRate(int view, double rate)
    {
        if (!ObserverView.ValidPlaybackRate(rate))
            throw new ArgumentOutOfRangeException(nameof(rate));
        _views[view] = _views[view] with { PlaybackRate = rate == 0 ? null : rate };
        SetPlaying(view, rate != 0);
    }

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
        ObserverView previous = _views[view];
        double first = (double)World.Ephemeris.Start.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        double last = (double)World.Ephemeris.End.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        if (seconds <= first)
            SetClock(view, World.Ephemeris.Start, false);
        else if (seconds >= last)
            SetClock(view, World.Ephemeris.End, false);
        else
            SetClock(view, previous.Epoch.AddSeconds(seconds), previous.Playing);
    }

    internal static double DefaultPlaybackRate(CameraMode mode) => mode switch
    {
        CameraMode.Probe => 1,
        CameraMode.Satellites or CameraMode.Mission or CameraMode.Timeline => 180,
        _ => JulianInstant.SecondsPerDay
    };

    public bool Tick(double elapsed)
    {
        bool changed = false;
        int start = Synchronized ? _clockOwner : 0;
        int end = Synchronized ? start + 1 : _views.Count;
        for (int view = start; view < end; view++)
        {
            if (_views[view].Playing)
            {
                double rate = _views[view].PlaybackRate ?? DefaultPlaybackRate(_views[view].Mode);
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
                    !camera.TryFrameBasis(next.Graph.Evaluate(previous.Epoch), DeltaT, out _);
                if (lostTarget || lostFrame)
                {
                    NavigationPlacement? placement = camera.Placement is { UsesFrame: true }
                        ? camera.ResolvePlacement(World.Graph.Evaluate(previous.Epoch), DeltaT) : camera.Placement;
                    navigation = camera with { Target = lostTarget ? null : target, Tracking = !lostTarget && camera.Tracking,
                        Placement = placement };
                    CancelFlight(index);
                }
            }
            _views[index] = previous with { Selection = selected, Mode = mode, Navigation = navigation };
        }
        World = next;
        return true;
    }

    private void SetClock(int view, JulianInstant epoch, bool playing)
    {
        if (!Synchronized)
        {
            _views[view] = _views[view] with { Epoch = epoch, Playing = playing };
            return;
        }
        _clockOwner = view;
        for (int index = 0; index < _views.Count; index++)
            _views[index] = _views[index] with { Epoch = epoch, Playing = playing };
    }

    private static bool Covers(SimulationWorld world, JulianInstant epoch) =>
        epoch.Scale == TimeScale.Tdb && epoch.CompareTo(world.Ephemeris.Start) >= 0 &&
        epoch.CompareTo(world.Ephemeris.End) <= 0;
}
