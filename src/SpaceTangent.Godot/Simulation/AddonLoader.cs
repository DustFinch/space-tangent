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
    PreparedMission? Mission, PreparedMission? Timeline, PreparedProbe? Probe, BodyTerrainAsset? Terrain, BodyTerrainAsset? MarsTerrain, IReadOnlyList<PreparedSscBody> SscBodies);

internal static class AddonLoader
{
    internal sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);
    internal sealed record DecodedMission(SpiceSpacecraft Track, DecodedScienceModel Model,
        IReadOnlyList<SpiceSensor> Sensors, SaturnSurface? Surface);
    internal sealed record DecodedTimeline(SscSampledSpacecraft Track, DecodedScienceModel Model, HuygensMission? Probe,
        DecodedScienceModel? ProbeModel);
    internal sealed record DecodedSscBody(SscBodyDefinition Body, DecodedScienceModel? Geometry);
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
        IReadOnlyList<Cached<IReadOnlyList<DecodedSscBody>>> BodyCatalogues, Cached<IReadOnlyList<SscSurfaceLocation>> Locations, Cached<DeepSkyIndex> DeepSky)
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

    internal static LocalDataPath SourcePath(LocalAddonSource source, LocalAddonSettingsFile file) =>
        new(file.Resolve(source), "", "", "", source.Kind != LocalAddonKind.Mission);

    // Godot path resolution runs on the main thread. File probes, hashing and decoding run on the worker.
    internal static Task<DecodedAddOns> Load(ISolarSystemEphemeris ephemeris, DecodedAddOns? previous,
        LocalAddonSettings settings, LocalAddonSettingsFile file, BrightStarCatalogue? brightStars, CancellationToken cancellation)
    {
        LocalDataPath[] science = Paths(LocalAddonKind.Science).ToArray();
        LocalDataPath[] bodySources = Paths(LocalAddonKind.BodyCatalog).ToArray();
        LocalDataPath? mission = First(LocalAddonKind.Mission);
        LocalDataPath? timeline = First(LocalAddonKind.Timeline);
        LocalDataPath? terrain = First(LocalAddonKind.Terrain);
        var planetPath = LocalDataPaths.Candidates("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        var pckPath = LocalDataPaths.Candidates("SPACETANGENT_PCK", "timeline/pck00011.tpc");
        var stellarPath = LocalDataPaths.Candidates("SPACETANGENT_SPATIAL_STARS", "sky/spatial-stars.tsv");
        var archivePath = LocalDataPaths.Candidates("SPACETANGENT_STELLAR_ARCHIVE", "sky/stellar-archive", directory: true);
        var stellarDefinitionsPath = LocalDataPaths.Candidates("SPACETANGENT_STC_ROOT", "sky/stc", directory: true);
        var locationPath = LocalDataPaths.Candidates("SPACETANGENT_SURFACE_LOCATIONS", "surface-locations", directory: true);
        var deepSkyPath = LocalDataPaths.Candidates("SPACETANGENT_DSC_ROOT", "sky/dsc", directory: true);
        return Task.Run(() =>
        {
            var satellites = science.Select(path => Cached<IReadOnlyList<DecodedSatellite>>.Load(
                previous?.Science.FirstOrDefault(source => source.Path == path), path,
                "Science satellites", ReadScience, cancellation)).ToArray();
            var bodies = bodySources.Select(path => Cached<IReadOnlyList<DecodedSscBody>>.Load(
                previous?.BodyCatalogues.FirstOrDefault(source => source.Path == path), path,
                "SSC body catalogue", ReadBodyCatalogue, cancellation)).ToArray();
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
            bool sameScience = previous is not null && previous.Science.SequenceEqual(satellites);
            bool sameBodies = previous is not null && previous.BodyCatalogues.SequenceEqual(bodies);
            if (previous is not null && sameScience && sameBodies && ReferenceEquals(previous.Mission, spice) &&
                ReferenceEquals(previous.Timeline, sampled) && ReferenceEquals(previous.Terrain, relief) &&
                ReferenceEquals(previous.MarsTerrain, marsTerrain) && ReferenceEquals(previous.Locations, locations) &&
                ReferenceEquals(previous.DeepSky, deepSky) && ReferenceEquals(previous.Stellar, stellar) &&
                ReferenceEquals(previous.StellarArchive, archive) && ReferenceEquals(previous.StellarDefinitions, definitions))
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
                bodies.SelectMany(source => source.Value).Select(body => body.Body), locations.Value, deepSky.Value));
            return new(world, sameScience ? previous!.Science : satellites, spice, sampled, relief, planets, marsTerrain, stellar, archive, definitions, sameBodies ? previous!.BodyCatalogues : bodies, locations, deepSky);
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
        IReadOnlyList<PreparedSscBody> bodies = previous is not null &&
            ReferenceEquals(previous.Decoded.BodyCatalogues, decoded.BodyCatalogues)
                ? previous.SscBodies : PublishSscBodies(decoded, previous);
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

    private static IReadOnlyList<PreparedSscBody> PublishSscBodies(DecodedAddOns decoded, AddonGeneration? previous)
    {
        var sources = decoded.BodyCatalogues.SelectMany(source => source.Value).ToDictionary(body => body.Body);
        var reusable = previous?.SscBodies.ToDictionary(body => body.Body.Definition);
        return Array.AsReadOnly(decoded.World.SscBodies.Select(body =>
        {
            if (reusable?.TryGetValue(body.Definition, out var ready) == true)
                return ready;
            var model = sources[body.Definition].Geometry;
            return new PreparedSscBody(body, model is null ? null : ScienceMeshLoader.Publish(model, body.Definition.Mesh!),
                model?.Diameter ?? 2);
        }).ToArray());
    }

    private static IReadOnlyList<DecodedSscBody> ReadBodyCatalogue(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        return Array.AsReadOnly(SscBodyCatalogue.LoadWithMpc(root, message => global::Godot.GD.PushWarning(message), cancellation).Select(body =>
        {
            cancellation.ThrowIfCancellationRequested();
            DecodedScienceModel? mesh = body.Mesh is { } file ? Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".cmod" => CmodMeshDecoder.Decode(root, file, cancellation),
                ".3ds" => StudioMeshDecoder.Decode(file, cancellation),
                _ => throw new NotSupportedException("This SSC body needs a binary CMOD or 3DS mesh.")
            } : null;
            return new DecodedSscBody(body, mesh);
        }).ToArray());
    }

    private static IReadOnlyList<DecodedSatellite> ReadScience(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        var satellites = new List<DecodedSatellite>();
        if (location.Explicit is not null || AddonDependencies.FileExists(catalog))
        {
            foreach (ScienceSatellite track in ScienceSatelliteCatalog.Load(catalog, root))
            {
                cancellation.ThrowIfCancellationRequested();
                satellites.Add(new(track, ScienceModelDecoder.Decode(track.MeshPath, cancellation)));
            }
        }
        return satellites.AsReadOnly();
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
