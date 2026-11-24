// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

// Builds one immutable world generation; query behavior remains on SimulationWorld.
internal sealed record SimulationWorldSources(
    ISolarSystemEphemeris Ephemeris,
    IEnumerable<ScienceSatellite> Satellites,
    SpiceSpacecraft? Mission,
    SscSampledSpacecraft? Timeline,
    HuygensMission? Probe,
    SpicePolynomialBodyFrame? MoonFrame,
    BrightStarCatalogue? BrightStars,
    PlanetEphemerides? Planets,
    bool HasMarsTerrain,
    StellarCatalogue? Stellar,
    IEnumerable<SscBodyDefinition>? BodyCatalog,
    IEnumerable<SscSurfaceLocation>? Locations,
    DeepSkyIndex? DeepSky,
    ExoplanetCatalogue? Exoplanets,
    IEnumerable<SampledMission>? SampledMissions,
    IEnumerable<LocalTleSatellite>? LocalSatellites,
    EarthPlaceCatalogue? EarthPlaces,
    DistantStarIndex? DistantStars);

internal sealed class SimulationWorldAssembly
{
    internal SimulationWorldAssembly(SimulationWorldSources sources, BlackHoleCatalogue blackHoles)
    {
        BlackHoles = blackHoles;
        var ephemeris = sources.Ephemeris;
        var satellites = sources.Satellites;
        var mission = sources.Mission;
        var timeline = sources.Timeline;
        var probe = sources.Probe;
        var moonFrame = sources.MoonFrame;
        var brightStars = sources.BrightStars;
        var planets = sources.Planets;
        var hasMarsTerrain = sources.HasMarsTerrain;
        var stellar = sources.Stellar;
        var bodyCatalog = sources.BodyCatalog;
        var locations = sources.Locations;
        var deepSky = sources.DeepSky;
        var exoplanets = sources.Exoplanets;
        var sampledMissions = sources.SampledMissions;
        var localSatellites = sources.LocalSatellites;
        var earthPlaces = sources.EarthPlaces;
        var distantStars = sources.DistantStars;

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
            .Concat([SolarSystemSample.SunId, SolarSystemSample.BarycenterId, SolarSystemSample.MoonId, SimulationWorld.CygnusCompanionId])
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

    private BlackHoleCatalogue BlackHoles { get; }
    internal ISolarSystemEphemeris Ephemeris { get; }
    internal PlanetEphemerides? Planets { get; }
    internal IReadOnlyList<string> SourceWarnings { get; }
    internal IReadOnlyList<ScienceSatellite> Satellites { get; }
    internal BrightStarCatalogue BrightStars { get; }
    internal bool HasMarsTerrain { get; }
    internal StellarCatalogue Stellar { get; }
    internal DeepSkyIndex DeepSky { get; }
    internal ExoplanetCatalogue Exoplanets { get; }
    internal Vector3d StellarOrigin { get; }
    internal ObjectId? MissionId { get; }
    internal string? MissionName { get; }
    internal ObjectId? TimelineId { get; }
    internal JulianInstant? TimelineStart { get; }
    internal SscHuygensTrack? Probe { get; }
    internal bool HasLunarTerrain { get; }
    internal IReadOnlyList<InstalledSscBody> SscBodies { get; }
    internal EarthPlaceCatalogue EarthPlaces { get; }
    internal DistantStarIndex? DistantStars { get; }
    internal ObjectCatalogue Catalogue { get; }
    internal WorldGraph Graph { get; }
    internal WorldGraph AlmanacGraph { get; }

    private readonly Dictionary<ObjectId, InstalledSscBody> _sscById;
    internal Dictionary<ObjectId, InstalledSscBody> SscById => _sscById;
    private readonly Dictionary<ObjectId, SampledMission> _sampledMissions;
    internal Dictionary<ObjectId, SampledMission> SampledMissions => _sampledMissions;
    private readonly Dictionary<ObjectId, LocalTleSatellite> _localSatellites;
    internal Dictionary<ObjectId, LocalTleSatellite> LocalSatellites => _localSatellites;
    private readonly Dictionary<ObjectId, SurfaceSite> _sitesById;
    internal Dictionary<ObjectId, SurfaceSite> SitesById => _sitesById;
    private readonly Dictionary<ObjectId, SurfaceSite[]> _sitesByBody;
    internal Dictionary<ObjectId, SurfaceSite[]> SitesByBody => _sitesByBody;

    private static T[] UniqueSources<T>(IEnumerable<T> sources, Func<T, ObjectId> id,
        Func<T, string> source, List<string> warnings)
    {
        var kept = new Dictionary<ObjectId, T>();
        foreach (T item in sources)
            if (!kept.TryAdd(id(item), item))
                warnings.Add($"Duplicate {id(item).Value}: kept {source(kept[id(item)])}; ignored {source(item)}.");
        return kept.Values.ToArray();
    }

    private void AddCompactObjects(WorldGraphBuilder graph)
    {
        Vector3d binary = default;
        foreach (BlackHole hole in BlackHoles.Entries)
        {
            Vector3d relative = hole.PositionKilometers;
            if (hole.Host is { } host)
            {
                string designation = host.Replace(" ", "", StringComparison.Ordinal);
                var galaxy = DeepSky.Objects.FirstOrDefault(item =>
                    item.Designation.Replace(" ", "", StringComparison.Ordinal).Equals(designation, StringComparison.OrdinalIgnoreCase));
                if (galaxy is not null)
                    relative = galaxy.PositionKilometers;
            }
            Vector3d position = StellarOrigin + relative;
            graph.AddTrajectory(hole.Id, _ => new CartesianState(position, default));
            if (hole.Id == SimulationWorld.CygnusLensId && hole.DiskAxis() is { } axis)
            {
                var transverse = Vector3d.Cross(axis, hole.Direction * -1);
                binary = position + transverse * (.2441 * 149_597_870.7 / transverse.Length);
            }
        }
        graph.AddTrajectory(SimulationWorld.CygnusCompanionId, _ => new CartesianState(binary, default));
    }
}
