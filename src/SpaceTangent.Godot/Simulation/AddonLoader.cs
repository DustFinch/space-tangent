// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Godot.Simulation;

internal sealed record AddonGeneration(AddonLoader.DecodedAddOns Decoded, IReadOnlyList<PreparedSatellite> Satellites,
    PreparedMission? Mission, PreparedMission? Timeline, PreparedProbe? Probe, BodyTerrainAsset? Terrain, BodyTerrainAsset? MarsTerrain, IReadOnlyList<PreparedImportedBody> ImportedBodies);

internal static partial class AddonLoader
{
    internal sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);
    internal sealed record DecodedMission(SpiceSpacecraft Track, DecodedScienceModel Model,
        IReadOnlyList<SpiceSensor> Sensors, SaturnSurface? Surface);
    internal sealed record DecodedTimeline(SscSampledSpacecraft Track, DecodedScienceModel Model, HuygensMission? Probe,
        DecodedScienceModel? ProbeModel);
    internal sealed record DecodedSscBody(SscBodyDefinition Body, DecodedScienceModel? Geometry, byte[]? Surface);
    internal sealed record DecodedSampledMission(SampledMission Track,
        IReadOnlyDictionary<string, DecodedScienceModel> Meshes, IReadOnlyDictionary<string, byte[]> Textures);
    internal sealed record DecodedTerrain(SpicePolynomialBodyFrame Frame, DecodedBodyTerrain Model);

    internal sealed record Cached<T>(LocalDataPath Path, AddonDependencies Dependencies, T Value)
    {
        internal static Cached<T> Load(Cached<T>? previous, LocalDataPath path,
            string source, Func<LocalDataPath, CancellationToken, T> read, CancellationToken cancellation)
        {
            try
            {
                if (previous?.Path == path && previous.Dependencies.IsCurrent(cancellation))
                    return previous;
                var (value, dependencies) = AddonDependencies.Capture(() => read(path, cancellation), cancellation);
                return new(path, dependencies, value);
            }
            catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog, path.Explicit, source: source))
            {
                throw;
            }
        }
    }

    internal sealed record DecodedAddOns(SimulationWorld World, IReadOnlyList<Cached<IReadOnlyList<DecodedSatellite>>> Science,
        Cached<DecodedMission?>? Mission, Cached<DecodedTimeline?>? Timeline, Cached<DecodedTerrain?>? Terrain,
        PlanetEphemerides Planets, Cached<DecodedBodyTerrain>? MarsTerrain, Cached<StellarCatalogue> Stellar,
        Cached<StellarCatalogue?> StellarArchive, Cached<StellarImport> StellarDefinitions,
        IReadOnlyList<Cached<IReadOnlyList<DecodedSscBody>>> BodyCatalogues, Cached<IReadOnlyList<SscSurfaceLocation>> Locations, Cached<DeepSkyIndex> DeepSky, Cached<ExoplanetCatalogue> Exoplanets,
        Cached<IReadOnlyList<DecodedSampledMission>> SampledMissions, Cached<IReadOnlyList<LocalTleSatellite>> LocalSatellites,
        Cached<EarthPlaceCatalogue> EarthPlaces, LocalDataPath SavedEarthPlacesPath, bool AllowAddonLua,
        IReadOnlyDictionary<ObjectId, PreparedMissionObservation> Observations, Cached<GalacticDustGrid?> GalacticDust, Cached<DistantStarIndex?> DistantStars)
    {
        internal bool HasSource(LocalAddonKind kind, LocalDataPath path) => kind switch
        {
            LocalAddonKind.Science => Science.Any(source => source.Path == path && source.Value.Count > 0),
            LocalAddonKind.Mission => Mission?.Path == path && Mission.Value is not null,
            LocalAddonKind.Timeline => Timeline?.Path == path && Timeline.Value is not null,
            LocalAddonKind.Terrain => Terrain?.Path == path && Terrain.Value is not null,
            LocalAddonKind.BodyCatalog => BodyCatalogues.Any(source => source.Path == path && source.Value.Count > 0),
            _ => false
        };
    }

    internal static LocalDataPath DefaultPath(LocalAddonKind kind) => kind switch
    {
        LocalAddonKind.Science => LocalDataPaths.Candidates("SPACETANGENT_SCIENCE_ROOT", "science", directory: true),
        LocalAddonKind.Mission => LocalDataPaths.Candidates("SPACETANGENT_MISSION_CATALOG", "mission/cassini.json"),
        LocalAddonKind.Timeline => LocalDataPaths.Candidates("SPACETANGENT_TIMELINE_ROOT", "timeline", directory: true),
        LocalAddonKind.Terrain => LocalDataPaths.Candidates("SPACETANGENT_TERRAIN_ROOT", "terrain", directory: true),
        LocalAddonKind.BodyCatalog => LocalDataPaths.Candidates("SPACETANGENT_SSC_ADDON_ROOT", "ssc-bodies", directory: true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static LocalDataPath SavedEarthPlacePath() =>
        LocalDataPaths.Candidates("SPACETANGENT_SAVED_EARTH_PLACES", "saved-sites.tsv");

    private static IEnumerable<string> SavedEarthPlaceSources(LocalDataPath paths)
    {
        if (paths.Explicit is { } explicitPath)
        {
            yield return explicitPath;
            yield break;
        }
        yield return AddonDependencies.FileExists(paths.Project) ? paths.Project : paths.Export;
        yield return paths.User;
    }

    internal static LocalDataPath SourcePath(LocalAddonSource source, LocalAddonSettingsFile file) =>
        new(file.Resolve(source), "", "", "", source.Kind != LocalAddonKind.Mission);

    // Godot path resolution runs on the main thread. File probes, hashing and decoding run on the worker.
    internal static Task<DecodedAddOns> Load(ISolarSystemEphemeris ephemeris, DecodedAddOns? previous,
        LocalAddonSettings settings, LocalAddonSettingsFile file, BrightStarCatalogue? brightStars, CancellationToken cancellation)
    {
        LocalDataPath[] science = Paths(LocalAddonKind.Science).ToArray();
        LocalDataPath[] bodySources = Paths(LocalAddonKind.BodyCatalog).ToArray();
        bool allowAddonLua = global::Godot.OS.GetCmdlineUserArgs().Contains("--allow-addon-lua", StringComparer.Ordinal) ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_ALLOW_ADDON_LUA") == "1";
        LocalDataPath? mission = First(LocalAddonKind.Mission);
        LocalDataPath? timeline = First(LocalAddonKind.Timeline);
        LocalDataPath? terrain = First(LocalAddonKind.Terrain);
        var planetPath = LocalDataPaths.Candidates("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        var pckPath = LocalDataPaths.Candidates("SPACETANGENT_PCK", "timeline/pck00011.tpc");
        var stellarPath = LocalDataPaths.Candidates("SPACETANGENT_SPATIAL_STARS", "sky/spatial-stars.tsv");
        var archivePath = LocalDataPaths.Candidates("SPACETANGENT_STELLAR_ARCHIVE", "sky/stellar-archive", directory: true);
        var stellarDefinitionsPath = LocalDataPaths.Candidates("SPACETANGENT_STC_ROOT", "sky/stc", directory: true);
        var exoplanetPath = LocalDataPaths.Candidates("SPACETANGENT_EXOPLANET_CATALOGUE", "sky/exoplanets.txt");
        var locationPath = LocalDataPaths.Candidates("SPACETANGENT_SURFACE_LOCATIONS", "surface-locations", directory: true);
        var deepSkyPath = LocalDataPaths.Candidates("SPACETANGENT_DSC_ROOT", "sky/dsc", directory: true);
        var dustPath = LocalDataPaths.Candidates("SPACETANGENT_DUST_GRID", "sky/dust-vergely2022.bin");
        var missionsPath = LocalDataPaths.Candidates("SPACETANGENT_SAMPLED_MISSIONS_ROOT", "sampled-missions", directory: true);
        var tlePath = LocalDataPaths.Candidates("SPACETANGENT_TLE_ROOT", "satellites", directory: true);
        var earthPlacesPath = LocalDataPaths.Candidates("SPACETANGENT_EARTH_PLACES", "sites.txt");
        var savedEarthPlacesPath = SavedEarthPlacePath();
        return Task.Run(() =>
        {
            var satellites = science.Select(path => Cached<IReadOnlyList<DecodedSatellite>>.Load(
                previous?.Science.FirstOrDefault(source => source.Path == path), path,
                "Science satellites", ReadScience, cancellation)).ToArray();
            var bodies = bodySources.Select(path => Cached<IReadOnlyList<DecodedSscBody>>.Load(
                previous?.AllowAddonLua == allowAddonLua ? previous.BodyCatalogues.FirstOrDefault(source => source.Path == path) : null,
                path, "SSC body catalogue", (source, token) => ReadBodyCatalogue(source, token, allowAddonLua), cancellation)).ToArray();
            var spice = Read(previous?.Mission, mission, "SPICE mission", ReadMission);
            var sampled = Read(previous?.Timeline, timeline, "Sampled timeline", ReadTimeline);
            var relief = Read(previous?.Terrain, terrain, "Lunar terrain", ReadTerrain);
            PlanetEphemerides planets = previous?.Planets ?? new(planetPath.Resolve(), pckPath.Resolve());
            string marsPath = Path.Combine(planetPath.Resolve(), "terrain-mars.bin");
            Cached<DecodedBodyTerrain>? marsTerrain = planets.HasOrientation(499) && File.Exists(marsPath)
                ? Read(previous?.MarsTerrain, new LocalDataPath(marsPath, "", "", "", false), "Mars terrain",
                    (path, token) => AddonDiagnostics.Read(AddonStage.Terrain, marsPath,
                        () => DecodedBodyTerrain.Load(path.Resolve(), 3396, token)))
                : null;
            cancellation.ThrowIfCancellationRequested();

            var locations = Cached<IReadOnlyList<SscSurfaceLocation>>.Load(previous?.Locations, locationPath, "Surface locations",
                (path, token) => SscSurfaceLocations.Load(path.Resolve(), token), cancellation);
            var deepSky = Cached<DeepSkyIndex>.Load(previous?.DeepSky, deepSkyPath, "Deep-sky catalogues",
                (path, token) => DeepSkyIndex.Load(path.Resolve(), token), cancellation);
            var galacticDust = Cached<GalacticDustGrid?>.Load(previous?.GalacticDust, dustPath, "Galactic dust grid",
                (path, _) => ReadGalacticDust(path.Resolve()), cancellation);
            var stellar = Cached<StellarCatalogue>.Load(previous?.Stellar, stellarPath, "Spatial star catalogue",
                (path, _) =>
                {
                    string file = path.Resolve();
                    return AddonDependencies.FileExists(file) ? StellarCatalogue.Load(file) : StellarCatalogue.Empty;
                }, cancellation);
            var archive = Cached<StellarCatalogue?>.Load(ReferenceEquals(previous?.Stellar, stellar) ? previous?.StellarArchive : null,
                archivePath, "Archived star catalogue", (path, token) =>
                {
                    string root = path.Resolve();
                    return AddonDependencies.FileExists(Path.Combine(root, "stars.bin")) ?
                        StellarCatalogue.LoadArchive(root, stellar.Value, token) : null;
                }, cancellation);
            bool sameStarBase = ReferenceEquals(previous?.Stellar, stellar) && ReferenceEquals(previous?.StellarArchive, archive);
            var definitions = Cached<StellarImport>.Load(sameStarBase ? previous?.StellarDefinitions : null,
                stellarDefinitionsPath, "STC stellar catalogues", (path, token) =>
                {
                    var result = (archive.Value ?? stellar.Value).WithStc(path.Resolve(), token);
                    if (result.AppliedRecords > 0 || result.DiagnosticCount > 0)
                    {
                        global::Godot.GD.Print($"SpaceTangent STC: {result.AppliedRecords:N0} effective records, {result.DiagnosticCount} diagnostics.");
                        foreach (string warning in result.Warnings)
                            global::Godot.GD.Print("SpaceTangent STC: " + warning);
                    }
                    return result;
                }, cancellation);
            var exoplanets = Cached<ExoplanetCatalogue>.Load(previous?.Exoplanets, exoplanetPath, "Exoplanet catalogue",
                (path, token) =>
                {
                    string file = path.Resolve();
                    return AddonDependencies.FileExists(file) ? ExoplanetCatalogue.Load(file, token) : ExoplanetCatalogue.Empty;
                }, cancellation);
            bool sameScience = previous is not null && previous.Science.SequenceEqual(satellites);
            var missions = Cached<IReadOnlyList<DecodedSampledMission>>.Load(sameScience ? previous?.SampledMissions : null, missionsPath,
                "Sampled mission catalogues", (path, token) => ReadSampledMissions(path, token,
                    satellites.SelectMany(source => source.Value).Select(item => item.Track).ToArray()), cancellation);
            var tle = Cached<IReadOnlyList<LocalTleSatellite>>.Load(previous?.LocalSatellites, tlePath,
                "Local TLE groups", (path, token) => LocalTleCatalogue.Load(path.Resolve(),
                    message => global::Godot.GD.Print("Local TLE: " + message), token), cancellation);
            var earthPlaces = Cached<EarthPlaceCatalogue>.Load(
                previous?.SavedEarthPlacesPath == savedEarthPlacesPath ? previous?.EarthPlaces : null,
                earthPlacesPath, "Earth observing places", (path, token) => EarthPlaceCatalogue.LoadWithSavedSources(path.Resolve(),
                    SavedEarthPlaceSources(savedEarthPlacesPath), message => global::Godot.GD.Print("Earth places: " + message), token), cancellation);
            DistantStarIndex? openedDistantStars = null;
            try
            {
                var distantStars = Cached<DistantStarIndex?>.Load(previous?.DistantStars, archivePath, "Distant star catalogue",
                    (path, _) =>
                    {
                        string root = path.Resolve();
                        string file = Path.Combine(root, "stars-far.bin");
                        if (AddonDependencies.FileExists(file))
                            openedDistantStars = DistantStarIndex.Load(file, Path.Combine(root, "stars-far-names.txt"));
                        return openedDistantStars;
                    }, cancellation);
                bool sameBodies = previous is not null && previous.AllowAddonLua == allowAddonLua && previous.BodyCatalogues.SequenceEqual(bodies);
                if (previous is not null && sameScience && sameBodies && ReferenceEquals(previous.Mission, spice) &&
                    ReferenceEquals(previous.Timeline, sampled) && ReferenceEquals(previous.Terrain, relief) &&
                    ReferenceEquals(previous.MarsTerrain, marsTerrain) && ReferenceEquals(previous.Locations, locations) &&
                    ReferenceEquals(previous.DeepSky, deepSky) && ReferenceEquals(previous.GalacticDust, galacticDust) && ReferenceEquals(previous.Stellar, stellar) &&
                    ReferenceEquals(previous.StellarArchive, archive) && ReferenceEquals(previous.DistantStars, distantStars) && ReferenceEquals(previous.StellarDefinitions, definitions) &&
                    ReferenceEquals(previous.Exoplanets, exoplanets) && ReferenceEquals(previous.SampledMissions, missions) &&
                    ReferenceEquals(previous.LocalSatellites, tle) && ReferenceEquals(previous.EarthPlaces, earthPlaces))
                    return previous;

                var tracks = new List<ScienceSatellite>();
                var ids = new HashSet<ObjectId>();
                foreach (var source in satellites)
                foreach (DecodedSatellite item in source.Value)
                {
                    if (!ids.Add(item.Track.Id))
                    {
                        var error = new InvalidDataException($"Two enabled science sources provide {item.Track.Id.Value}.");
                        AddonDiagnostics.Annotate(error, AddonStage.Catalog, item.Track.MeshPath, source: "Science satellites");
                        throw error;
                    }
                    tracks.Add(item.Track);
                }
                var world = AddonDiagnostics.Read(AddonStage.World, null, () => new SimulationWorld(ephemeris, tracks,
                    spice?.Value?.Track, sampled?.Value?.Track, sampled?.Value?.Probe, relief?.Value?.Frame, brightStars, planets, marsTerrain is not null, definitions.Value.Catalogue,
                    bodies.SelectMany(source => source.Value).Select(body => body.Body), locations.Value, deepSky.Value, exoplanets.Value, missions.Value.Select(mission => mission.Track), tle.Value, earthPlaces.Value, distantStars.Value));
                var observations = PrepareObservations(world, missions.Value, cancellation);
                var decoded = new DecodedAddOns(world, sameScience ? previous!.Science : satellites, spice, sampled, relief, planets, marsTerrain, stellar, archive, definitions, sameBodies ? previous!.BodyCatalogues : bodies, locations, deepSky, exoplanets, missions, tle, earthPlaces, savedEarthPlacesPath, allowAddonLua, observations, galacticDust, distantStars);
                openedDistantStars = null;
                return decoded;
            }
            finally
            {
                openedDistantStars?.Dispose();
            }
        }, cancellation);

        Cached<T>? Read<T>(Cached<T>? cached, LocalDataPath? path, string category, Func<LocalDataPath, CancellationToken, T> reader) =>
            path is { } available ? Cached<T>.Load(cached, available, category, reader, cancellation) : null;

        IEnumerable<LocalDataPath> Paths(LocalAddonKind kind)
        {
            if (settings.UsesDefault(kind))
                yield return DefaultPath(kind);
            foreach (var source in settings.Sources.Where(source => source.Kind == kind && source.Enabled))
                yield return SourcePath(source, file);
        }

        LocalDataPath? First(LocalAddonKind kind) => Paths(kind).Select(path => (LocalDataPath?)path).SingleOrDefault();
    }

    private static GalacticDustGrid? ReadGalacticDust(string path)
    {
        if (!AddonDependencies.FileExists(path))
            return null;
        const int maximum = 56 + GalacticDustGrid.MaximumVoxels * 2;
        using var stream = AddonDependencies.OpenRead(path, maximum);
        if (stream.Length > maximum)
            throw new InvalidDataException("The galactic extinction grid exceeds its size limit.");
        byte[] data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return GalacticDustGrid.Read(data);
    }

    internal static AddonGeneration Publish(DecodedAddOns decoded, AddonGeneration? previous)
    {
        if (previous is not null && ReferenceEquals(previous.Decoded, decoded))
            return previous;

        IReadOnlyList<PreparedSatellite> satellites = previous is not null && ReferenceEquals(previous.Decoded.Science, decoded.Science)
            ? previous.Satellites : PublishSatellites(decoded.Science, previous?.Satellites ?? []);
        PreparedMission? mission = previous is not null && ReferenceEquals(previous.Decoded.Mission, decoded.Mission)
            ? previous.Mission : decoded.Mission?.Value is { } geometry
                ? new(decoded.World.MissionId!.Value, ScienceMeshLoader.Publish(geometry.Model, geometry.Track.MeshPath), Scale(geometry.Model),
                    SimulationViewport.CreateFrusta(geometry.Sensors), geometry.Surface, geometry.Sensors)
                : null;
        PreparedMission? timeline = previous?.Timeline;
        PreparedProbe? probe = previous?.Probe;
        if (!ReferenceEquals(previous?.Decoded.Timeline, decoded.Timeline))
        {
            var track = decoded.Timeline?.Value;
            timeline = track is null ? null : new(track.Track.Id, ScienceMeshLoader.Publish(track.Model, track.Track.MeshPath),
                Scale(track.Model), null, null, []);
            probe = track?.ProbeModel is { } probeModel && track.Probe is { } probeMission
                ? new(probeMission.Track, ScienceMeshLoader.Publish(probeModel, probeMission.Track.MeshPath),
                    (float)(24 / probeModel.Diameter)) : null;
        }
        BodyTerrainAsset? terrain = previous is not null && ReferenceEquals(previous.Decoded.Terrain, decoded.Terrain)
            ? previous.Terrain : decoded.Terrain?.Value is { } relief ? BodyTerrainAsset.Publish(relief.Model) : null;
        BodyTerrainAsset? marsTerrain = previous is not null && ReferenceEquals(previous.Decoded.MarsTerrain, decoded.MarsTerrain)
            ? previous.MarsTerrain : decoded.MarsTerrain?.Value is { } mars ? BodyTerrainAsset.Publish(mars) : null;
        IReadOnlyList<PreparedImportedBody> bodies = previous is not null &&
            ReferenceEquals(previous.Decoded.BodyCatalogues, decoded.BodyCatalogues) &&
            ReferenceEquals(previous.Decoded.SampledMissions, decoded.SampledMissions) &&
            (ReferenceEquals(previous.Decoded.Observations, decoded.Observations) ||
             (previous.Decoded.Observations.Count == 0 && decoded.Observations.Count == 0))
                ? previous.ImportedBodies : PublishImportedBodies(decoded, previous);
        return new(decoded, satellites, mission, timeline, probe, terrain, marsTerrain, bodies);

        static float Scale(DecodedScienceModel model) => (float)(18 / model.Diameter);

        static IReadOnlyList<PreparedSatellite> PublishSatellites(IReadOnlyList<Cached<IReadOnlyList<DecodedSatellite>>> sources,
            IReadOnlyList<PreparedSatellite> previous)
        {
            var reusable = new Dictionary<ScienceSatellite, PreparedSatellite>(ReferenceEqualityComparer.Instance);
            foreach (PreparedSatellite satellite in previous)
                reusable.Add(satellite.Track, satellite);
            return Array.AsReadOnly(sources.SelectMany(source => source.Value).Select(item =>
                reusable.TryGetValue(item.Track, out PreparedSatellite? satellite) ? satellite :
                    new PreparedSatellite(item.Track, ScienceMeshLoader.Publish(item.Model, item.Track.MeshPath),
                        Scale(item.Model))).ToArray());
        }
    }

    private static IReadOnlyList<PreparedImportedBody> PublishImportedBodies(DecodedAddOns decoded, AddonGeneration? previous)
    {
        var sources = decoded.BodyCatalogues.SelectMany(source => source.Value).ToDictionary(body => body.Body);
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
                publishedSscTextures.Add(map, texture = PublishMissionTexture(map, pixels));
            bool lit = body.Kind == CatalogueKind.Moon && definition.Class.Equals("minormoon", StringComparison.OrdinalIgnoreCase);
            global::Godot.ArrayMesh? mesh = null;
            if (model is not null && !publishedSscMeshes.TryGetValue((model, definition.SurfaceMap, lit), out mesh))
                publishedSscMeshes.Add((model, definition.SurfaceMap, lit),
                    mesh = ScienceMeshLoader.Publish(model, definition.Mesh!, litBody: lit, bodySurface: texture));
            return new PreparedImportedBody(body.Id, body.Kind, definition.Color, definition.RadiusKilometers,
                definition.MeshRotation, definition.MeshCenter, mesh, model?.Diameter ?? 2)
            {
                EllipsoidRadiiKilometers = definition.EllipsoidRadiiKilometers,
                Surface = texture,
                MeshSpan = model is null || definition.EllipsoidRadiiKilometers is null ? null : MeshSpan(model)
            };
        });
        var textures = new Dictionary<string, global::Godot.Texture2D>(StringComparer.Ordinal);
        var missions = decoded.SampledMissions.Value.Select(item =>
        {
            var track = item.Track;
            if (track.Observation is null && ReferenceEquals(decoded.SampledMissions, previous?.Decoded.SampledMissions) &&
                priorAssets?.TryGetValue(track.Id, out var ready) == true)
                return ready;
            var models = item.Meshes.ToDictionary(pair => pair.Key,
                pair => (Mesh: ScienceMeshLoader.Publish(pair.Value, pair.Key), pair.Value.Diameter));
            PublishMissionTextures(item.Textures, textures);
            IReadOnlyList<PreparedMissionGeometry>? geometry = track.Geometry?.Select(span =>
            {
                var shape = span.Shape;
                var model = shape.MeshPath is { } path ? models[path] : default;
                return new PreparedMissionGeometry(span.Start, span.End, shape.RadiusKilometers,
                    shape.MeshRotation, shape.MeshOffset, model.Mesh, model.Diameter, shape.Sensor,
                    shape.ParticleEmitters?.Select(emitter => new PreparedParticleEmitter(emitter,
                        emitter.TexturePath is { } image ? textures[image] : null)).ToArray())
                {
                    Axes = shape.Axes,
                    EllipsoidRadiiKilometers = shape.EllipsoidRadiiKilometers,
                    GlobeMaterial = shape.GlobeTexturePath is { } map ? new global::Godot.StandardMaterial3D
                    {
                        AlbedoTexture = textures[map], Roughness = .85f
                    } : null
                };
            }).ToArray();
            return new PreparedImportedBody(track.Id, CatalogueKind.Spacecraft, new(.8, .8, .8), track.RadiusKilometers, Basis3d.Identity, default,
                null, 2, geometry, decoded.Observations.GetValueOrDefault(track.Id));
        });
        return Array.AsReadOnly(bodies.Concat(missions).ToArray());
    }

    private static IReadOnlyList<DecodedSampledMission> ReadSampledMissions(LocalDataPath location, CancellationToken cancellation,
        IReadOnlyList<ScienceSatellite> science)
    {
        string root = location.Resolve();
        if (!AddonDependencies.DirectoryExists(root))
            return [];
        var catalogues = AddonDependencies.ListFiles(root, "*.json");
        string directory = Path.Combine(root, "catalog");
        if (AddonDependencies.DirectoryExists(directory))
            catalogues = [.. catalogues, .. AddonDependencies.ListFiles(directory, "*.json")];
        if (catalogues.Length > 64)
            throw new InvalidDataException("The sampled mission directory may contain at most 64 catalogues.");
        var tracks = new List<DecodedSampledMission>();
        var textureCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int textureBytes = 0;
        foreach (SampledMission mission in SampledMissionCatalog.Load(catalogues.Order(StringComparer.Ordinal), root, Resolve,
                     message => global::Godot.GD.Print("Sampled mission: " + message), cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            var meshes = new Dictionary<string, DecodedScienceModel>(StringComparer.Ordinal);
            foreach (string file in (mission.Geometry ?? []).Select(span => span.Shape.MeshPath).OfType<string>().Distinct())
                meshes.Add(file, ReadAddonMesh(root, file, cancellation));
            tracks.Add(new(mission, meshes, ReadMissionTextures(mission, textureCache, ref textureBytes, cancellation)));
        }
        return tracks.AsReadOnly();

        ObjectId? Resolve(string name)
        {
            if (name.Equals("Sun", StringComparison.OrdinalIgnoreCase) || name.Equals("Sol", StringComparison.OrdinalIgnoreCase))
                return SolarSystemSample.SunId;
            if (name.Equals("Moon", StringComparison.OrdinalIgnoreCase))
                return SolarSystemSample.MoonId;
            return PlanetCatalogue.Planets.FirstOrDefault(planet => planet.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id ??
                NaturalBodyCatalogue.Bodies.FirstOrDefault(body => body.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id ??
                science.FirstOrDefault(track => track.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;
        }
    }

    private static IReadOnlyList<DecodedSscBody> ReadBodyCatalogue(LocalDataPath location, CancellationToken cancellation, bool allowLua)
    {
        string root = location.Resolve();
        var scripts = allowLua ? new LuaCatalogueFactories() : null;
        var decodedMeshes = new Dictionary<(string File, bool Native), DecodedScienceModel>();
        var decodedSurfaces = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int textureBytes = 0;
        return Array.AsReadOnly(SscBodyCatalogue.LoadWithMpc(root, message => global::Godot.GD.Print("SSC bodies: " + message),
            cancellation, scripts).Select(body =>
        {
            cancellation.ThrowIfCancellationRequested();
            DecodedScienceModel? mesh = null;
            bool native = body.Class.Equals("minormoon", StringComparison.OrdinalIgnoreCase);
            if (body.Mesh is { } file && !decodedMeshes.TryGetValue((file, native), out mesh))
                decodedMeshes.Add((file, native), mesh = ReadAddonMesh(root, file, cancellation, native));
            byte[]? pixels = null;
            if (body.SurfaceMap is { } map && !decodedSurfaces.TryGetValue(map, out pixels))
            {
                pixels = ReadMissionTexture(map, ref textureBytes);
                decodedSurfaces.Add(map, pixels);
            }
            return new DecodedSscBody(body, mesh, pixels);
        }).ToArray());
    }

    private static Vector3d MeshSpan(DecodedScienceModel model)
    {
        Vector3d lower = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d upper = lower * -1;
        foreach (var surface in model.Surfaces)
        foreach (var p in surface.Positions.Span)
        {
            lower = new(Math.Min(lower.X, p.X), Math.Min(lower.Y, p.Y), Math.Min(lower.Z, p.Z));
            upper = new(Math.Max(upper.X, p.X), Math.Max(upper.Y, p.Y), Math.Max(upper.Z, p.Z));
        }
        return upper - lower;
    }

    private static DecodedScienceModel ReadAddonMesh(string root, string file, CancellationToken cancellation, bool nativeBody = false) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".obj" => ScienceModelDecoder.Decode(file, cancellation),
            ".cmod" => nativeBody ? CmodMeshDecoder.DecodeNativeBody(root, file, cancellation) : CmodMeshDecoder.Decode(root, file, cancellation),
            ".cms" => DisplacedSphereDecoder.Decode(file, cancellation),
            ".3ds" => StudioMeshDecoder.Decode(file, cancellation),
            ".glb" or ".gltf" => PackedModelDecoder.Decode(root, file, cancellation),
            _ => throw new NotSupportedException("The add-on mesh needs OBJ, CMOD, CMS, 3DS, glTF or GLB.")
        };

    private static IReadOnlyList<DecodedSatellite> ReadScience(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        string pack = Path.Combine(root, "pack.json");
        IReadOnlyList<ScienceSatellite> tracks = AddonDependencies.FileExists(pack)
            ? PackedSatelliteCatalog.Load(root, message => global::Godot.GD.Print("Packed satellites: " + message))
            : location.Explicit is not null || AddonDependencies.FileExists(catalog)
                ? ScienceSatelliteCatalog.Load(catalog, root) : [];
        return Array.AsReadOnly(tracks.Select(track =>
        {
            cancellation.ThrowIfCancellationRequested();
            return new DecodedSatellite(track, Path.GetExtension(track.MeshPath).ToLowerInvariant() is ".glb" or ".gltf"
                ? PackedModelDecoder.Decode(root, track.MeshPath, cancellation) : ScienceModelDecoder.Decode(track.MeshPath, cancellation));
        }).ToArray());
    }

    private static DecodedMission? ReadMission(LocalDataPath location, CancellationToken cancellation)
    {
        string? path = location.Explicit;
        if (path is null)
        {
            string json = location.Resolve();
            if (AddonDependencies.FileExists(json))
                path = json;
            else
            {
                string ssc = location.WithExtension(".ssc").Resolve();
                if (AddonDependencies.FileExists(ssc))
                    path = ssc;
            }
        }
        if (path is null)
            return null;
        string fullPath = Path.GetFullPath(path);
        return AddonDiagnostics.Read(AddonStage.Catalog, fullPath, () => ReadMissionFile(fullPath, cancellation));
    }

    private static DecodedMission ReadMissionFile(string fullPath, CancellationToken cancellation)
    {
        string root = Path.GetDirectoryName(fullPath)!;
        SpiceCatalogResult catalog = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".ssc" => new(SscSpiceCatalog.Load(SscSources(fullPath, root), root), []),
            ".json" => SpiceJsonCatalog.Load(fullPath, root),
            _ => throw new NotSupportedException("Use an SSC or JSON mission catalogue.")
        };
        IReadOnlyList<SpiceSpacecraft> entries = catalog.Spacecraft;
        if (entries.Count != 1 || entries[0].Target != -82 || entries[0].Center != 6 || entries[0].AttitudeFrame is null)
            throw new NotSupportedException("This mission view expects a single Cassini track at Saturn with CK pointing.");
        SpiceSpacecraft mission = entries[0];
        if (!mission.TrySample(SimulationSession.MissionPreview, out _) || !mission.TryAttitude(SimulationSession.MissionPreview, out _))
            throw new InvalidDataException("The mission kernels do not cover the Cassini orbit insertion preview.");

        string centers = Path.Combine(root, "sat480.bsp"), pck = Path.Combine(root, "pck00011.tpc");
        SaturnSurface? surface = AddonDependencies.FileExists(centers) && AddonDependencies.FileExists(pck) ? new(centers, pck) : null;
        return new(mission, StudioMeshDecoder.Decode(mission.MeshPath, cancellation), catalog.Sensors, surface);
    }

    private static DecodedTimeline? ReadTimeline(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string catalog = Path.Combine(root, "ssc", "cassini.ssc");
        if (location.Explicit is null && !AddonDependencies.FileExists(catalog))
            return null;
        string[] sources = SscSources(catalog, root);
        var track = AddonDiagnostics.Read(AddonStage.Catalog, catalog,
            () => SscSampledCatalog.Load(sources, root, "Sol/Saturn/Cassini", cancellation));
        var geometry = StudioMeshDecoder.Decode(track.MeshPath, cancellation);
        string sat083 = Path.Combine(root, "sat083.bsp"), pck = Path.Combine(root, "pck00011.tpc");
        if (!AddonDependencies.FileExists(sat083) || !AddonDependencies.FileExists(pck))
            return new(track, geometry, null, null);

        var frame = new SpicePolynomialBodyFrame(pck, 606);
        var probe = AddonDiagnostics.Read(AddonStage.Catalog, catalog,
            () => SscHuygensTrack.Load(sources, root, track, frame, cancellation));
        return new(track, geometry, new(probe, new(sat083), frame),
            StudioMeshDecoder.Decode(probe.MeshPath, cancellation, centerOnBounds: true));
    }

    private static DecodedTerrain? ReadTerrain(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string path = Path.Combine(root, "terrain-moon.bin");
        if (!AddonDependencies.FileExists(path))
        {
            if (location.Explicit is not null)
                throw new FileNotFoundException("The terrain pack needs terrain-moon.bin.", path);
            return null;
        }
        var frame = new SpicePolynomialBodyFrame(Path.Combine(root, "pck00011.tpc"), 301);
        return new(frame, AddonDiagnostics.Read(AddonStage.Terrain, path,
            () => DecodedBodyTerrain.Load(path, frame.RadiiKilometers.X, cancellation)));
    }

    private static string[] SscSources(string catalog, string root)
    {
        string directory = Path.Combine(root, "overrides");
        return AddonDependencies.DirectoryExists(directory) ? [catalog, .. AddonDependencies.ListFiles(directory, "*.ssc")] : [catalog];
    }
}
