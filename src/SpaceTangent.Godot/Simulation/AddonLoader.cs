// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;

using SpaceTangent.Application.Addons;
using static SpaceTangent.Application.Addons.AddonPreparation;

namespace SpaceTangent.Godot.Simulation;

internal sealed record AddonGeneration(AddonPreparation.DecodedAddOns Decoded, IReadOnlyList<PreparedSatellite> Satellites,
    PreparedMission? Mission, PreparedMission? Timeline, PreparedProbe? Probe, BodyTerrainAsset? Terrain, BodyTerrainAsset? MarsTerrain, IReadOnlyList<PreparedImportedBody> ImportedBodies) : IDisposable
{
    internal PublishedRenderAssets Resources { get; set; } = new([]);
    public void Dispose() => Resources.Dispose();

    internal IEnumerable<global::Godot.Resource?> ResourceRoots()
    {
        foreach (var satellite in Satellites) yield return satellite.Mesh;
        yield return Mission?.Mesh; yield return Mission?.Frusta;
        yield return Timeline?.Mesh; yield return Timeline?.Frusta; yield return Probe?.Mesh;
        yield return Terrain?.Heights; yield return MarsTerrain?.Heights;
        foreach (var body in ImportedBodies)
        {
            yield return body.Model; yield return body.Surface; yield return body.Rings?.Texture;
            if (body.Geometry is null) continue;
            foreach (var span in body.Geometry)
            {
                yield return span.Model; yield return span.TiledGlobe; yield return span.GlobeMaterial;
                yield return span.Swarm?.Mesh;
                if (span.Particles is not null)
                    foreach (var particle in span.Particles) yield return particle.Texture;
            }
        }
    }
}


internal static partial class AddonLoader
{
    private static readonly AddonPreparation Preparation = new(message => global::Godot.GD.Print(message),
        message => global::Godot.GD.PushWarning(message));

    internal static AddonSourcePath DefaultPath(AddonSourceKind kind) => kind switch
    {
        AddonSourceKind.Science => LocalDataPaths.Candidates("SPACETANGENT_SCIENCE_ROOT", "science", directory: true),
        AddonSourceKind.Mission => LocalDataPaths.Candidates("SPACETANGENT_MISSION_CATALOG", "mission/cassini.json"),
        AddonSourceKind.Timeline => LocalDataPaths.Candidates("SPACETANGENT_TIMELINE_ROOT", "timeline", directory: true),
        AddonSourceKind.Terrain => LocalDataPaths.Candidates("SPACETANGENT_TERRAIN_ROOT", "terrain", directory: true),
        AddonSourceKind.BodyCatalog => LocalDataPaths.Candidates("SPACETANGENT_SSC_ADDON_ROOT", "ssc-bodies", directory: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static string SatelliteUpdatesPath()
    {
        var path = LocalDataPaths.Candidates("SPACETANGENT_TLE_UPDATES", "satellite-updates", directory: true);
        return path.Explicit ?? path.User;
    }

    internal static AddonSourcePath SavedEarthPlacePath() =>
        LocalDataPaths.Candidates("SPACETANGENT_SAVED_EARTH_PLACES", "saved-sites.tsv");

    internal static AddonSourcePath SourcePath(LocalAddonSource source, LocalAddonSettingsFile file) =>
        new(file.Resolve(source), "", "", "", source.Kind != AddonSourceKind.Mission);

    // Godot path resolution runs on the main thread. File probes, hashing and decoding run on the worker.
    internal static Task<DecodedAddOns> Load(ISolarSystemEphemeris ephemeris, DecodedAddOns? previous,
        LocalAddonSettings settings, LocalAddonSettingsFile file, BrightStarCatalogue? brightStars, CancellationToken cancellation)
    {
        AddonSourcePath[] science = Paths(AddonSourceKind.Science).ToArray();
        AddonSourcePath[] bodySources = Paths(AddonSourceKind.BodyCatalog).ToArray();
        bool allowAddonLua = global::Godot.OS.GetCmdlineUserArgs().Contains("--allow-addon-lua", StringComparer.Ordinal) ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_ALLOW_ADDON_LUA") == "1";
        AddonSourcePath? mission = First(AddonSourceKind.Mission);
        AddonSourcePath? timeline = First(AddonSourceKind.Timeline);
        AddonSourcePath? terrain = First(AddonSourceKind.Terrain);
        var planetPath = LocalDataPaths.Candidates("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        var pckPath = LocalDataPaths.Candidates("SPACETANGENT_PCK", "timeline/pck00011.tpc");
        var stellarPath = LocalDataPaths.Candidates("SPACETANGENT_SPATIAL_STARS", "sky/spatial-stars.tsv");
        var archivePath = LocalDataPaths.Candidates("SPACETANGENT_STELLAR_ARCHIVE", "sky/stellar-archive", directory: true);
        var stellarDefinitionsPath = LocalDataPaths.Candidates("SPACETANGENT_STC_ROOT", "sky/stc", directory: true);
        var stellarCorrectionsPath = LocalDataPaths.Candidates("SPACETANGENT_STELLAR_CORRECTIONS", "sky/corrections", directory: true);
        var exoplanetPath = LocalDataPaths.Candidates("SPACETANGENT_EXOPLANET_CATALOGUE", "sky/exoplanets.txt");
        var locationPath = LocalDataPaths.Candidates("SPACETANGENT_SURFACE_LOCATIONS", "surface-locations", directory: true);
        var deepSkyPath = LocalDataPaths.Candidates("SPACETANGENT_DSC_ROOT", "sky/dsc", directory: true);
        var dustPath = LocalDataPaths.Candidates("SPACETANGENT_DUST_GRID", "sky/dust-vergely2022.bin");
        var missionsPath = LocalDataPaths.Candidates("SPACETANGENT_SAMPLED_MISSIONS_ROOT", "sampled-missions", directory: true);
        var tlePath = LocalDataPaths.Candidates("SPACETANGENT_TLE_ROOT", "satellites", directory: true);
        string tleUpdates = SatelliteUpdatesPath();
        var earthPlacesPath = LocalDataPaths.Candidates("SPACETANGENT_EARTH_PLACES", "sites.txt");
        var savedEarthPlacesPath = SavedEarthPlacePath();
        var inputs = new AddonPreparationInputs(
            science,
            bodySources,
            mission,
            timeline,
            terrain,
            planetPath,
            pckPath,
            stellarPath,
            archivePath,
            stellarDefinitionsPath,
            stellarCorrectionsPath,
            exoplanetPath,
            locationPath,
            deepSkyPath,
            dustPath,
            missionsPath,
            tlePath,
            tleUpdates,
            earthPlacesPath,
            savedEarthPlacesPath,
            allowAddonLua,
            settings.DisabledPacks);
        return Task.Run(() => Preparation.Prepare(ephemeris, previous, inputs, brightStars, cancellation), cancellation);

        IEnumerable<AddonSourcePath> Paths(AddonSourceKind kind)
        {
            if (settings.UsesDefault(kind))
                yield return DefaultPath(kind);
            foreach (var source in settings.Sources.Where(source => source.Kind == kind && source.Enabled))
                yield return SourcePath(source, file);
        }


        AddonSourcePath? First(AddonSourceKind kind) => Paths(kind).Select(path => (AddonSourcePath?)path).SingleOrDefault();
    }

    internal static AddonGeneration Publish(DecodedAddOns decoded, AddonGeneration? previous)
    {
        if (previous is not null && ReferenceEquals(previous.Decoded, decoded))
            return previous;

        using var publication = new GpuAssetPublication();
        IReadOnlyList<PreparedSatellite> satellites = previous is not null && ReferenceEquals(previous.Decoded.Science, decoded.Science)
            ? previous.Satellites : PublishSatellites(decoded.Science, previous?.Satellites ?? [], publication);
        PreparedMission? mission = previous is not null && ReferenceEquals(previous.Decoded.Mission, decoded.Mission)
            ? previous.Mission : decoded.Mission is { } geometry
                ? new(decoded.World.MissionId!.Value, ScienceMeshLoader.Publish(geometry.Model, geometry.Track.MeshPath, publication: publication), Scale(geometry.Model),
                    SimulationViewport.CreateFrusta(geometry.Sensors, publication), geometry.Surface, geometry.Sensors)
                : null;
        PreparedMission? timeline = previous?.Timeline;
        PreparedProbe? probe = previous?.Probe;
        if (!ReferenceEquals(previous?.Decoded.Timeline, decoded.Timeline))
        {
            var track = decoded.Timeline?.Value;
            timeline = track is null ? null : new(track.Track.Id, ScienceMeshLoader.Publish(track.Model, track.Track.MeshPath, publication: publication),
                Scale(track.Model), null, null, []);
            probe = track?.ProbeModel is { } probeModel && track.Probe is { } probeMission
                ? new(probeMission.Track, ScienceMeshLoader.Publish(probeModel, probeMission.Track.MeshPath, publication: publication),
                    (float)(24 / probeModel.Diameter)) : null;
        }
        BodyTerrainAsset? terrain = previous is not null && ReferenceEquals(previous.Decoded.Terrain, decoded.Terrain)
            ? previous.Terrain : decoded.Terrain?.Value is { } relief ? BodyTerrainAsset.Publish(relief.Model, publication) : null;
        BodyTerrainAsset? marsTerrain = previous is not null && ReferenceEquals(previous.Decoded.MarsTerrain, decoded.MarsTerrain)
            ? previous.MarsTerrain : decoded.MarsTerrain?.Value is { } mars ? BodyTerrainAsset.Publish(mars, publication) : null;
        IReadOnlyList<PreparedImportedBody> bodies = previous is not null &&
            ReferenceEquals(previous.Decoded.BodyCatalogues, decoded.BodyCatalogues) &&
            ReferenceEquals(previous.Decoded.SampledMissions, decoded.SampledMissions) &&
            (ReferenceEquals(previous.Decoded.Observations, decoded.Observations) ||
             (previous.Decoded.Observations.Count == 0 && decoded.Observations.Count == 0))
                ? previous.ImportedBodies : PublishImportedBodies(decoded, previous, publication);
        var result = new AddonGeneration(decoded, satellites, mission, timeline, probe, terrain, marsTerrain, bodies);
        result.Resources = publication.Commit(result.ResourceRoots());
        return result;

        static float Scale(DecodedScienceModel model) => (float)(18 / model.Diameter);

        static IReadOnlyList<PreparedSatellite> PublishSatellites(IReadOnlyList<Cached<IReadOnlyList<DecodedSatellite>>> sources,
            IReadOnlyList<PreparedSatellite> previous, GpuAssetPublication publication)
        {
            var reusable = new Dictionary<ScienceSatellite, PreparedSatellite>(ReferenceEqualityComparer.Instance);
            foreach (PreparedSatellite satellite in previous)
                reusable.Add(satellite.Track, satellite);
            return Array.AsReadOnly(sources.SelectMany(source => source.Value).DistinctBy(item => item.Track.Id).Select(item =>
                reusable.TryGetValue(item.Track, out PreparedSatellite? satellite) ? satellite :
                    new PreparedSatellite(item.Track, ScienceMeshLoader.Publish(item.Model, item.Track.MeshPath, publication: publication),
                        Scale(item.Model))).ToArray());
        }
    }

    internal static IReadOnlyList<StellarEntry> PhotometricStars(SimulationWorld world, ObjectId host)
    {
        var primary = world.FindStellarTarget(host);
        if (primary is null)
            return [];
        if (!primary.IsBarycenter)
            return primary.DistanceParsecs > 0 ? [primary] : [];
        return world.Stellar.Stars.Where(star => star.BarycenterId == primary.Id && !star.IsBarycenter &&
            star.DistanceParsecs > 0).ToArray();
    }

    private static IReadOnlyList<PreparedImportedBody> PublishImportedBodies(DecodedAddOns decoded, AddonGeneration? previous, GpuAssetPublication publication)
    {
        var sources = decoded.BodyCatalogues.Bodies.ToDictionary(body => body.Body);
        var priorDefinitions = previous?.Decoded.World.SscBodies.ToDictionary(body => body.Id, body => body.Definition);
        var priorAssets = previous?.ImportedBodies.ToDictionary(body => body.Id);
        var publishedSscMeshes = new Dictionary<(DecodedScienceModel Model, string? Map, bool Lit), global::Godot.ArrayMesh>();
        var publishedSscTextures = new Dictionary<string, global::Godot.Texture2D>(StringComparer.Ordinal);
        var bodies = decoded.World.SscBodies.Where(body => !body.Definition.Class.Equals("invisible", StringComparison.OrdinalIgnoreCase)).Select(body =>
        {
            SscBodyDefinition definition = body.Definition;
            if (priorDefinitions?.TryGetValue(body.Id, out var prior) == true && definition == prior &&
                priorAssets?.TryGetValue(body.Id, out var ready) == true)
                return ready;
            var input = sources[definition];
            var model = input.Geometry;
            global::Godot.Texture2D? texture = null;
            if (definition.SurfaceMap is { } map && input.Surface is { } pixels &&
                !publishedSscTextures.TryGetValue(map, out texture))
                publishedSscTextures.Add(map, texture = PublishMissionTexture(map, pixels, publication));
            bool lit = body.Kind == CatalogueKind.Moon && definition.Class.Equals("minormoon", StringComparison.OrdinalIgnoreCase);
            global::Godot.ArrayMesh? mesh = null;
            if (model is not null && !publishedSscMeshes.TryGetValue((model, definition.SurfaceMap, lit), out mesh))
                publishedSscMeshes.Add((model, definition.SurfaceMap, lit),
                    mesh = ScienceMeshLoader.Publish(model, definition.Mesh!, litBody: lit, bodySurface: texture, publication: publication));
            var bounds = model is not null &&
                (definition.EllipsoidRadiiKilometers is not null || body.Kind == CatalogueKind.Spacecraft)
                ? model.MeshBounds() : ((Vector3d Center, Vector3d Span)?)null;
            return new PreparedImportedBody(body.Id, body.Kind, definition.Color, definition.RadiusKilometers,
                definition.MeshRotation, definition.MeshCenter, mesh, model?.Diameter ?? 2)
            {
                EllipsoidRadiiKilometers = definition.EllipsoidRadiiKilometers,
                Surface = texture,
                StellarHost = body.StellarHost,
                PhotometricParent = body.Kind is (CatalogueKind.Planet or CatalogueKind.Moon or
                    CatalogueKind.DwarfPlanet or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.Spacecraft) ? body.Parent : null,
                GeometricAlbedo = definition.GeometricAlbedo ?? .5,
                LabelOrbitRadius = body.Orbit?.Points.Select(point => point.Length).DefaultIfEmpty().Max() ??
                    definition.OrbitApoapsisKilometers ?? 0,
                MinorMoon = definition.Class.Equals("minormoon", StringComparison.OrdinalIgnoreCase),
                PhotometricStars = body.StellarHost is { } host ? PhotometricStars(decoded.World, host) : null,
                Rings = definition.Rings is { } rings && input.RingMap is { } ringMap
                    ? new(rings, PublishMissionTexture(rings.TexturePath, ringMap, publication)) : null,
                MeshSpan = definition.EllipsoidRadiiKilometers is not null ? bounds?.Span : null,
                ModelBounds = body.Kind == CatalogueKind.Spacecraft ? bounds : null
            };
        });
        var textures = new Dictionary<string, global::Godot.Texture2D>(StringComparer.Ordinal);
        var swarmResources = new Dictionary<string, (global::Godot.ArrayMesh Mesh, global::Godot.Texture2D Data)>(StringComparer.OrdinalIgnoreCase);
        var missions = decoded.SampledMissions.Tracks.DistinctBy(item => item.Track.Id).Select(item =>
        {
            var track = item.Track;
            if (track.Observation is null && ReferenceEquals(decoded.SampledMissions, previous?.Decoded.SampledMissions) &&
                priorAssets?.TryGetValue(track.Id, out var ready) == true)
                return ready;
            var models = item.Meshes.ToDictionary(pair => pair.Key,
                pair => (Mesh: ScienceMeshLoader.Publish(pair.Value, pair.Key, publication: publication), pair.Value.Diameter));
            PublishMissionTextures(item.Textures, textures, publication);
            IReadOnlyList<PreparedMissionGeometry>? geometry = track.Geometry?.Select(span =>
            {
                var shape = span.Shape;
                var model = shape.MeshPath is { } path ? models[path] : default;
                return new PreparedMissionGeometry(span.Start, span.End, shape.RadiusKilometers,
                    shape.MeshRotation, shape.MeshOffset, model.Mesh, model.Diameter, shape.Sensor,
                    shape.ParticleEmitters?.Select(emitter => new PreparedParticleEmitter(emitter,
                        emitter.TexturePath is { } image ? textures[image] : null)).ToArray())
                {
                    MeshScale = shape.MeshScale,
                    Axes = shape.Axes,
                    VectorOverlay = shape.VectorOverlay,
                    Swarm = shape.Swarm is { } swarm ? MissionSwarmRendering.Publish(swarm, swarmResources, publication) : null,
                    EllipsoidRadiiKilometers = shape.EllipsoidRadiiKilometers,
                    TiledGlobe = shape.SurfaceMaps is { } maps ? PublishTiledMissionGlobe(maps, item.Tiles,
                        shape.GlobeTexturePath is { } fallback ? textures[fallback] : null, publication) : null,
                    GlobeMaterial = shape.GlobeTexturePath is { } map ? PublishMissionGlobeMaterial(textures[map], publication) : null
                };
            }).ToArray();
            return new PreparedImportedBody(track.Id, CatalogueKind.Spacecraft, new(.8, .8, .8), track.RadiusKilometers, Basis3d.Identity, default,
                null, 2, geometry, decoded.Observations.GetValueOrDefault(track.Id));
        });
        return Array.AsReadOnly(bodies.Concat(missions).ToArray());
    }

}
