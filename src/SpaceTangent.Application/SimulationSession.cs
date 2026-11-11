// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

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
}

public sealed record HuygensMission(SscHuygensTrack Track, SpkKernel SaturnSystem, SpicePolynomialBodyFrame TitanFrame);

public sealed class SimulationWorld
{
    public SimulationWorld(De440Ephemeris ephemeris, IEnumerable<ScienceSatellite> satellites,
        SpiceSpacecraft? mission = null, SscSampledSpacecraft? timeline = null, HuygensMission? probe = null,
        SpicePolynomialBodyFrame? moonFrame = null, BrightStarCatalogue? brightStars = null, PlanetEphemerides? planets = null, bool hasMarsTerrain = false)
    {
        Ephemeris = ephemeris;
        Satellites = Array.AsReadOnly(satellites.ToArray());
        BrightStars = brightStars ?? BrightStarCatalogue.Empty;
        HasMarsTerrain = hasMarsTerrain;
        Catalogue = new(Satellites, BrightStars);
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
        Graph = graph.Build();
    }

    public De440Ephemeris Ephemeris { get; }
    public IReadOnlyList<ScienceSatellite> Satellites { get; }
    public ObjectCatalogue Catalogue { get; }
    public BrightStarCatalogue BrightStars { get; }
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
    public int ViewCount => _views.Count;
    public ObserverView this[int view] => _views[view];

    public SimulationScenario CaptureScenario(int activeView) =>
        new(Array.AsReadOnly(_views.ToArray()), Synchronized, _clockOwner, activeView, DeltaT);

    public void RestoreScenario(SimulationScenario scenario)
    {
        ObserverView[] views = scenario.Views.ToArray();
        if (views.Length is < 1 or > 2 || scenario.ClockOwner < 0 || scenario.ClockOwner >= views.Length ||
            scenario.ActiveView < 0 || scenario.ActiveView >= views.Length)
            throw new InvalidDataException("The scenario refers to an unavailable view.");
        if (!Enum.IsDefined(scenario.DeltaT))
            throw new InvalidDataException("The scenario has an unknown ΔT model.");
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
            if (view.Terrain is { } terrain && !terrain.IsValid())
                throw new InvalidDataException("The scenario has invalid lunar camera coordinates or field width.");
            if (view.Mode == CameraMode.Satellites && World.Satellites.Count == 0)
                throw new InvalidDataException("The scenario needs the Earth satellite add-ons.");
            if (view.Selection is { } selection && World.Catalogue.Find(selection) is null)
                throw new InvalidDataException($"The selected object {selection.Value} is missing from the current world.");
            if (view.Navigation is { } camera &&
                (!camera.IsValid() || (camera.Target is { } target && World.Catalogue.Find(target) is null)))
                throw new InvalidDataException("The scenario has an invalid camera pose or unavailable camera target.");
        }
        ObserverView owner = views[scenario.ClockOwner];
        if (scenario.Synchronized && views.Any(view => view.Epoch != owner.Epoch || view.Playing != owner.Playing))
            throw new InvalidDataException("Synchronized scenario clocks must agree.");

        _views.Clear();
        _views.AddRange(views);
        Array.Clear(_flights);
        Synchronized = scenario.Synchronized;
        _clockOwner = scenario.ClockOwner;
        DeltaT = scenario.DeltaT;
    }

    public void SetEarthObserver(int view, EarthObserverCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view] with { EarthObserver = camera };
    }

    public void SetSkyLayers(int view, SkyLayerSettings sky) => _views[view] = _views[view] with { Sky = sky };

    public void Split()
    {
        if (_views.Count == 1)
            _views.Add(_views[0]);
    }

    public void CloseSecondView()
    {
        if (_views.Count == 2)
            _views.RemoveAt(1);
        _flights[1] = null;
        _clockOwner = 0;
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

    public bool Tick(double elapsed)
    {
        bool changed = false;
        int start = Synchronized ? _clockOwner : 0;
        int end = Synchronized ? start + 1 : _views.Count;
        for (int view = start; view < end; view++)
        {
            if (_views[view].Playing)
            {
                double rate = _views[view].Mode switch
                {
                    CameraMode.Probe => 1,
                    CameraMode.Satellites or CameraMode.Mission or CameraMode.Timeline => 180,
                    _ => JulianInstant.SecondsPerDay
                };
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
            if (navigation is { Target: { } target } camera && next.Catalogue.Find(target) is null)
            {
                navigation = camera with { Target = null, Tracking = false };
                CancelFlight(index);
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
