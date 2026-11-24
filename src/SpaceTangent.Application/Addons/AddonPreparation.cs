// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Application.Addons;

public sealed record AddonPreparationInputs(
    AddonSourcePath[] Science,
    AddonSourcePath[] BodySources,
    AddonSourcePath? Mission,
    AddonSourcePath? Timeline,
    AddonSourcePath? Terrain,
    AddonSourcePath PlanetPath,
    AddonSourcePath PckPath,
    AddonSourcePath StellarPath,
    AddonSourcePath ArchivePath,
    AddonSourcePath StellarDefinitionsPath,
    AddonSourcePath StellarCorrectionsPath,
    AddonSourcePath ExoplanetPath,
    AddonSourcePath LocationPath,
    AddonSourcePath DeepSkyPath,
    AddonSourcePath DustPath,
    AddonSourcePath MissionsPath,
    AddonSourcePath TlePath,
    string TleUpdates,
    AddonSourcePath EarthPlacesPath,
    AddonSourcePath SavedEarthPlacesPath,
    bool AllowAddonLua,
    string[] DisabledPacks);

public sealed partial class AddonPreparation
{
    private readonly Action<string> _report, _warning;

    public AddonPreparation(Action<string>? report = null, Action<string>? warning = null)
    {
        _report = report ?? (_ => { });
        _warning = warning ?? (_ => { });
    }

    public sealed record DecodedDeepSky(DeepSkyIndex Index, IReadOnlyDictionary<ObjectId, IReadOnlyList<DecodedModelSprite>> Sprites);
    public sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);
    public sealed record DecodedMission(SpiceSpacecraft Track, DecodedScienceModel Model,
        IReadOnlyList<SpiceSensor> Sensors, SaturnSurface? Surface);
    public sealed record DecodedTimeline(SscSampledSpacecraft Track, DecodedScienceModel Model, HuygensMission? Probe,
        DecodedScienceModel? ProbeModel);
    public sealed record SscInstallation(AddonSourcePath[] Paths, AddonSourcePath MissionsPath, AddonSourcePath? DedicatedPath,
        AddonDependencies Dependencies, IReadOnlyList<DecodedSscBody> Bodies, DecodedMissionContent Missions, DecodedMission? Dedicated);
    public sealed record DecodedSscBody(SscBodyDefinition Body, DecodedScienceModel? Geometry, byte[]? Surface, byte[]? RingMap);
    public sealed record DecodedSampledMission(SampledMission Track,
        IReadOnlyDictionary<string, DecodedScienceModel> Meshes, IReadOnlyDictionary<string, byte[]> Textures, IReadOnlyDictionary<string, byte[]> Tiles);
    public sealed record DecodedMissionContent(IReadOnlyList<DecodedSampledMission> Tracks, IReadOnlyList<MissionAnnotation> Annotations, IReadOnlyList<MissionViewpoint> Viewpoints);
    public sealed record DecodedTerrain(SpicePolynomialBodyFrame Frame, DecodedBodyTerrain Model);

    public sealed record Cached<T>(AddonSourcePath Path, AddonDependencies Dependencies, T Value)
    {
        internal static Cached<T> Load(Cached<T>? previous, AddonSourcePath path,
            string source, Func<AddonSourcePath, CancellationToken, T> read, CancellationToken cancellation)
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

    public sealed record DecodedAddOns(SimulationWorld World, IReadOnlyList<Cached<IReadOnlyList<DecodedSatellite>>> Science,
        DecodedMission? Mission, Cached<DecodedTimeline?>? Timeline, Cached<DecodedTerrain?>? Terrain,
        PlanetEphemerides Planets, Cached<DecodedBodyTerrain>? MarsTerrain, Cached<StellarCatalogue> Stellar,
        Cached<StellarCatalogue?> StellarArchive, Cached<StellarImport> StellarDefinitions, Cached<StellarImport> StellarCorrections,
        SscInstallation BodyCatalogues, Cached<IReadOnlyList<SscSurfaceLocation>> Locations, Cached<DecodedDeepSky> DeepSky, Cached<ExoplanetCatalogue> Exoplanets,
        DecodedMissionContent SampledMissions, Cached<IReadOnlyList<LocalTleSatellite>> LocalSatellites, string SatelliteUpdates,
        Cached<EarthPlaceCatalogue> EarthPlaces, AddonSourcePath SavedEarthPlacesPath, bool AllowAddonLua,
        IReadOnlyDictionary<ObjectId, DecodedMissionObservation> Observations, Cached<GalacticDustGrid?> GalacticDust, Cached<DistantStarIndex?> DistantStars)
    {
        public IReadOnlyDictionary<AddonSourcePath, string[]> FilteredPacks { get; init; } =
            new Dictionary<AddonSourcePath, string[]>();

        public bool HasSource(AddonSourceKind kind, AddonSourcePath path) => kind switch
        {
            AddonSourceKind.Science => Science.Any(source => source.Path == path && source.Value.Count > 0),
            AddonSourceKind.Mission => BodyCatalogues.DedicatedPath == path && Mission is not null,
            AddonSourceKind.Timeline => Timeline?.Path == path && Timeline.Value is not null,
            AddonSourceKind.Terrain => Terrain?.Path == path && Terrain.Value is not null,
            AddonSourceKind.BodyCatalog => BodyCatalogues.Paths.Contains(path) && BodyCatalogues.Bodies.Count > 0,
            _ => false
        };
    }

    public DecodedAddOns Prepare(ISolarSystemEphemeris ephemeris, DecodedAddOns? previous,
        AddonPreparationInputs inputs, BrightStarCatalogue? brightStars, CancellationToken cancellation)
    {
        var science = inputs.Science;
        var bodySources = inputs.BodySources;
        var mission = inputs.Mission;
        var timeline = inputs.Timeline;
        var terrain = inputs.Terrain;
        var planetPath = inputs.PlanetPath;
        var pckPath = inputs.PckPath;
        var stellarPath = inputs.StellarPath;
        var archivePath = inputs.ArchivePath;
        var stellarDefinitionsPath = inputs.StellarDefinitionsPath;
        var stellarCorrectionsPath = inputs.StellarCorrectionsPath;
        var exoplanetPath = inputs.ExoplanetPath;
        var locationPath = inputs.LocationPath;
        var deepSkyPath = inputs.DeepSkyPath;
        var dustPath = inputs.DustPath;
        var missionsPath = inputs.MissionsPath;
        var tlePath = inputs.TlePath;
        var tleUpdates = inputs.TleUpdates;
        var earthPlacesPath = inputs.EarthPlacesPath;
        var savedEarthPlacesPath = inputs.SavedEarthPlacesPath;
        var allowAddonLua = inputs.AllowAddonLua;
        var disabledPacks = inputs.DisabledPacks;
        var filteredPacks = new Dictionary<AddonSourcePath, string[]>();
        science = science.Where(Enabled).ToArray();
        bodySources = bodySources.Where(Enabled).ToArray();
        if (mission is { } missionSource && !Enabled(missionSource)) mission = null;
        if (timeline is { } timelineSource && !Enabled(timelineSource)) timeline = null;
        if (terrain is { } terrainSource && !Enabled(terrainSource)) terrain = null;
        bool Enabled(AddonSourcePath path)
        {
            if (disabledPacks.Length == 0)
                return true;
            var keys = AddonPackIdentity.For(path).Intersect(disabledPacks, StringComparer.Ordinal).ToArray();
            if (keys.Length == 0)
                return true;
            filteredPacks[path] = keys;
            return false;
        }
        var satellites = science.Select(path => Cached<IReadOnlyList<DecodedSatellite>>.Load(
            previous?.Science.FirstOrDefault(source => source.Path == path), path,
            "Science satellites", ReadScience, cancellation)).ToArray();
        bool sameScience = previous is not null && previous.Science.SequenceEqual(satellites);
        PlanetEphemerides planets = previous?.Planets ?? new(planetPath.Resolve(), pckPath.Resolve());
        var bodies = LoadBodyCatalogues(bodySources, missionsPath, mission,
            sameScience && previous?.AllowAddonLua == allowAddonLua ? previous.BodyCatalogues : null,
            satellites.SelectMany(source => source.Value).Select(item => item.Track).ToArray(), allowAddonLua, planets.CatalogueModels(ephemeris), cancellation);
        var missions = bodies.Missions;
        var spice = bodies.Dedicated;
        var sampled = Read(previous?.Timeline, timeline, "Sampled timeline", ReadTimeline);
        var relief = Read(previous?.Terrain, terrain, "Lunar terrain", ReadTerrain);
        string marsPath = Path.Combine(planetPath.Resolve(), "terrain-mars.bin");
        Cached<DecodedBodyTerrain>? marsTerrain = planets.HasOrientation(499) && File.Exists(marsPath)
            ? Read(previous?.MarsTerrain, new AddonSourcePath(marsPath, "", "", "", false), "Mars terrain",
                (path, token) => AddonDiagnostics.Read(AddonStage.Terrain, marsPath,
                    () => DecodedBodyTerrain.Load(path.Resolve(), 3396, token)))
            : null;
        cancellation.ThrowIfCancellationRequested();

        var locations = Cached<IReadOnlyList<SscSurfaceLocation>>.Load(previous?.Locations, locationPath, "Surface locations",
            (path, token) => SscSurfaceLocations.Load(path.Resolve(), token), cancellation);
        var deepSky = Cached<DecodedDeepSky>.Load(previous?.DeepSky, deepSkyPath, "Deep-sky catalogues",
            (path, token) => ReadDeepSky(path.Resolve(), token), cancellation);
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
                var result = (archive.Value ?? stellar.Value).WithStc(path.Resolve(),
                    archive.Value is null ? null : stellar.Value, token);
                if (result.AppliedRecords > 0 || result.DiagnosticCount > 0)
                {
                    _report($"SpaceTangent STC: {result.AppliedRecords:N0} effective records, {result.DiagnosticCount} diagnostics.");
                    foreach (string warning in result.Warnings)
                        _report("SpaceTangent STC: " + warning);
                }
                return result;
            }, cancellation);
        var corrections = Cached<StellarImport>.Load(
            ReferenceEquals(previous?.StellarDefinitions, definitions) ? previous?.StellarCorrections : null,
            stellarCorrectionsPath, "Stellar distance corrections", (path, token) =>
            {
                var result = definitions.Value.Catalogue.WithDistanceCorrectionPacks(path.Resolve(), token);
                if (result.AppliedRecords > 0 || result.DiagnosticCount > 0)
                {
                    _report($"Stellar distance corrections: {result.AppliedRecords} applied, {result.DiagnosticCount} diagnostics.");
                    foreach (string warning in result.Warnings)
                        _report("Stellar distance corrections: " + warning);
                }
                return result;
            }, cancellation);
        var exoplanets = Cached<ExoplanetCatalogue>.Load(previous?.Exoplanets, exoplanetPath, "Exoplanet catalogue",
            (path, token) =>
            {
                string file = path.Resolve();
                return AddonDependencies.FileExists(file) ? ExoplanetCatalogue.Load(file, token) : ExoplanetCatalogue.Empty;
            }, cancellation);

        var tle = Cached<IReadOnlyList<LocalTleSatellite>>.Load(
            previous?.SatelliteUpdates == tleUpdates ? previous.LocalSatellites : null, tlePath,
            "Local TLE groups", (path, token) => LocalTleCatalogue.LoadSources([path.Resolve(), tleUpdates],
                message => _report("Local TLE: " + message), token), cancellation);
        var earthPlaces = Cached<EarthPlaceCatalogue>.Load(
            previous?.SavedEarthPlacesPath == savedEarthPlacesPath ? previous?.EarthPlaces : null,
            earthPlacesPath, "Earth observing places", (path, token) => EarthPlaceCatalogue.LoadWithSavedSources(path.Resolve(),
                SavedEarthPlaceSources(savedEarthPlacesPath), message => _report("Earth places: " + message), token), cancellation);
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
            bool sameBodies = ReferenceEquals(previous?.BodyCatalogues, bodies);
            if (previous is not null && sameScience && sameBodies && ReferenceEquals(previous.Mission, spice) &&
                ReferenceEquals(previous.Timeline, sampled) && ReferenceEquals(previous.Terrain, relief) &&
                ReferenceEquals(previous.MarsTerrain, marsTerrain) && ReferenceEquals(previous.Locations, locations) &&
                ReferenceEquals(previous.DeepSky, deepSky) && ReferenceEquals(previous.GalacticDust, galacticDust) && ReferenceEquals(previous.Stellar, stellar) &&
                ReferenceEquals(previous.StellarArchive, archive) && ReferenceEquals(previous.DistantStars, distantStars) && ReferenceEquals(previous.StellarDefinitions, definitions) &&
                ReferenceEquals(previous.StellarCorrections, corrections) && ReferenceEquals(previous.Exoplanets, exoplanets) && ReferenceEquals(previous.SampledMissions, missions) &&
                ReferenceEquals(previous.LocalSatellites, tle) && ReferenceEquals(previous.EarthPlaces, earthPlaces) &&
                previous.FilteredPacks.Count == filteredPacks.Count && filteredPacks.All(pair =>
                    previous.FilteredPacks.TryGetValue(pair.Key, out var keys) && keys.SequenceEqual(pair.Value)))
                return previous;

            var tracks = satellites.SelectMany(source => source.Value).Select(item => item.Track).ToArray();
            var world = AddonDiagnostics.Read(AddonStage.World, null, () => new SimulationWorld(ephemeris, tracks,
                spice?.Track, sampled?.Value?.Track, sampled?.Value?.Probe, relief?.Value?.Frame, brightStars, planets, marsTerrain is not null, corrections.Value.Catalogue,
                bodies.Bodies.Select(body => body.Body), locations.Value, deepSky.Value.Index, exoplanets.Value, missions.Tracks.Select(mission => mission.Track), tle.Value, earthPlaces.Value, distantStars.Value));
            foreach (string warning in world.SourceWarnings)
                _warning(warning);
            var observations = PrepareObservations(world,
                missions.Tracks.DistinctBy(item => item.Track.Id), cancellation);
            var decoded = new DecodedAddOns(world, sameScience ? previous!.Science : satellites, spice, sampled, relief, planets, marsTerrain, stellar, archive, definitions, corrections, bodies, locations, deepSky, exoplanets, missions, tle, tleUpdates, earthPlaces, savedEarthPlacesPath, allowAddonLua, observations, galacticDust, distantStars) { FilteredPacks = filteredPacks };
            openedDistantStars = null;
            return decoded;
        }
        finally
        {
            openedDistantStars?.Dispose();
        }

        Cached<T>? Read<T>(Cached<T>? cached, AddonSourcePath? path, string category, Func<AddonSourcePath, CancellationToken, T> reader) =>
            path is { } available ? Cached<T>.Load(cached, available, category, reader, cancellation) : null;

    }

    private IEnumerable<string> SavedEarthPlaceSources(AddonSourcePath paths)
    {
        if (paths.Explicit is { } explicitPath)
        {
            yield return explicitPath;
            yield break;
        }
        yield return AddonDependencies.FileExists(paths.Project) ? paths.Project : paths.Export;
        yield return paths.User;
    }

    private GalacticDustGrid? ReadGalacticDust(string path)
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

    private (IReadOnlyList<DecodedSscBody> Bodies, DecodedMissionContent Missions, DecodedMission? Dedicated) ReadMissionCatalogues(
        IEnumerable<string> bodyRoots, AddonSourcePath location, AddonSourcePath? dedicatedPath, IReadOnlyList<ScienceSatellite> science,
        bool allowLua, SscCustomModels custom, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string[] catalogues = AddonDependencies.DirectoryExists(root) ? FindMissionCatalogues(root) : [];
        string? dedicated = dedicatedPath is { } path ? FindDedicatedMission(path) : null;
        string[]? overlays = dedicated is not null && Path.GetExtension(dedicated).Equals(".ssc", StringComparison.OrdinalIgnoreCase)
            ? SscSources(dedicated, Path.GetDirectoryName(dedicated)!) : null;
        var imported = SscBodyCatalogue.LoadWithMissions(bodyRoots, catalogues, root, Resolve,
            message => _report("SSC bodies: " + message),
            message => _report("Sampled mission: " + message), cancellation,
            allowLua ? new LuaCatalogueFactories() : null, dedicated, overlays, custom);
        var mission = imported.DedicatedMission is { } data
            ? AddonDiagnostics.Read(AddonStage.Catalog, dedicated, () => DecodeDedicatedMission(dedicated!, data, cancellation))
            : null;
        return (DecodeBodyCatalogues(imported.Bodies, cancellation), DecodeSampledMissions(root, imported.Missions, cancellation), mission);

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

    private string[] FindMissionCatalogues(string root)
    {
        var catalogues = AddonDependencies.ListFiles(root, "*.json");
        string directory = Path.Combine(root, "catalog");
        if (AddonDependencies.DirectoryExists(directory))
            catalogues = [.. catalogues, .. AddonDependencies.ListFiles(directory, "*.json")];
        // Installed mission packs retain their own resource folders under the shared mission root.
        foreach (string folder in AddonDependencies.ListDirectories(root))
        {
            if (folder == directory || (AddonDependencies.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                continue;
            string pack = Path.Combine(folder, "pack.json");
            if (AddonDependencies.FileExists(pack))
                catalogues = [.. catalogues, pack];
            if (catalogues.Length > 64)
                break;
        }
        if (catalogues.Length > 64)
            throw new InvalidDataException("The sampled mission directory may contain at most 64 catalogues.");
        return catalogues.Order(StringComparer.Ordinal).ToArray();
    }

    private DecodedMissionContent DecodeSampledMissions(string root, SampledMissionContent content,
        CancellationToken cancellation)
    {
        var tracks = new List<DecodedSampledMission>();
        var textureCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var tileCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int textureBytes = 0, tileBytes = 0;
        foreach (SampledMission mission in content.Missions)
        {
            cancellation.ThrowIfCancellationRequested();
            var meshes = new Dictionary<string, DecodedScienceModel>(StringComparer.Ordinal);
            foreach (string file in (mission.Geometry ?? []).Select(span => span.Shape.MeshPath).OfType<string>().Distinct())
                meshes.Add(file, ReadAddonMesh(root, file, cancellation));
            var track = mission;
            var metrics = meshes.ToDictionary(pair => pair.Key, pair => ReadMissionMeshMetrics(pair.Value), StringComparer.Ordinal);
            double? navigationRadius = null;
            if (mission.GeometryBounds is { Complete: true } bounds)
            {
                foreach (string file in bounds.Meshes.Select(term => term.Path).Distinct())
                    if (!metrics.ContainsKey(file))
                        metrics.Add(file, ReadMissionMeshMetrics(ReadAddonMesh(root, file, cancellation)));
                navigationRadius = bounds.Resolve(file => metrics[file]);
            }
            IReadOnlyList<MissionGeometrySpan>? geometry = mission.Geometry?.Select(span =>
            {
                var shape = span.Shape;
                if (shape.MeshPath is not { } path)
                    return span;
                var (extent, originRadius) = metrics[path];
                var bound = new MissionMeshBound(path, shape.RadiusKilometers, shape.MeshOffset) { Scale = shape.MeshScale };
                double physicalRadius = Math.Abs(bound.InstanceRadius(extent, originRadius));
                if (!double.IsFinite(physicalRadius))
                    throw new InvalidDataException("A mission mesh has a nonfinite physical radius.");
                return span with { Shape = shape with { MeshScale = bound.ScaleForExtent(extent), RadiusKilometers = physicalRadius } };
            }).ToArray();
            if (navigationRadius is null && geometry?.Any(span => span.Shape.MeshScale.HasValue) == true)
                navigationRadius = geometry.Max(span => span.Shape.RadiusKilometers);
            track = mission with { Geometry = geometry, NavigationRadiusKilometers = navigationRadius };
            tracks.Add(new(track, meshes, ReadMissionTextures(mission, textureCache, ref textureBytes, cancellation),
                ReadMissionTiles(mission, tileCache, ref tileBytes, cancellation)));
        }
        return new(tracks.AsReadOnly(), content.Annotations, content.Viewpoints);
    }

    private SscInstallation LoadBodyCatalogues(AddonSourcePath[] paths, AddonSourcePath missionsPath,
        AddonSourcePath? dedicatedPath, SscInstallation? previous,
        IReadOnlyList<ScienceSatellite> science, bool allowLua, SscCustomModels custom, CancellationToken cancellation)
    {
        if (previous is not null && previous.MissionsPath == missionsPath && previous.DedicatedPath == dedicatedPath && previous.Paths.SequenceEqual(paths) &&
            previous.Dependencies.IsCurrent(cancellation))
            return previous;
        try
        {
            var (content, dependencies) = AddonDependencies.Capture(
                () => ReadMissionCatalogues(paths.Select(path => path.Resolve()), missionsPath, dedicatedPath, science, allowLua, custom, cancellation),
                cancellation);
            return new SscInstallation(paths, missionsPath, dedicatedPath, dependencies, content.Bodies, content.Missions, content.Dedicated);
        }
        catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Catalog, source: "Mission catalogues"))
        {
            throw;
        }
    }

    private IReadOnlyList<DecodedSscBody> DecodeBodyCatalogues(IReadOnlyList<SscBodyDefinition> bodies,
        CancellationToken cancellation)
    {
        var decodedMeshes = new Dictionary<(string File, string Root, bool Native), DecodedScienceModel>();
        var decodedSurfaces = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int textureBytes = 0;
        return Array.AsReadOnly(bodies.Select(body =>
        {
            cancellation.ThrowIfCancellationRequested();
            DecodedScienceModel? mesh = null;
            bool native = body.Class.Equals("minormoon", StringComparison.OrdinalIgnoreCase);
            if (body.Mesh is { } file && body.MeshRoot is { } root &&
                !decodedMeshes.TryGetValue((file, root, native), out mesh))
                decodedMeshes.Add((file, root, native), mesh = ReadAddonMesh(root, file, cancellation, native));
            byte[]? pixels = null;
            if (body.SurfaceMap is { } map && !decodedSurfaces.TryGetValue(map, out pixels))
            {
                pixels = ReadMissionTexture(map, ref textureBytes);
                decodedSurfaces.Add(map, pixels);
            }
            byte[]? ringMap = null;
            if (body.Rings is { } rings && !decodedSurfaces.TryGetValue(rings.TexturePath, out ringMap))
            {
                ringMap = ReadMissionTexture(rings.TexturePath, ref textureBytes);
                decodedSurfaces.Add(rings.TexturePath, ringMap);
            }
            return new DecodedSscBody(body, mesh, pixels, ringMap);
        }).ToArray());
    }

    private (float Extent, float OriginRadius) ReadMissionMeshMetrics(DecodedScienceModel model)
    {
        var (_, span) = model.MeshBounds();
        float squaredRadius = 0;
        foreach (var surface in model.Surfaces)
        foreach (var vertex in surface.Positions.Span)
            Include(vertex);
        foreach (var sprite in model.Sprites)
            Include(sprite.Position);
        return ((float)Math.Max(span.X, Math.Max(span.Y, span.Z)), MathF.Sqrt(squaredRadius));

        void Include(Vector3d vertex)
        {
            float x = (float)vertex.X, y = (float)vertex.Y, z = (float)vertex.Z;
            squaredRadius = MathF.Max(squaredRadius, x * x + y * y + z * z);
        }
    }

    private DecodedScienceModel ReadAddonMesh(string root, string file, CancellationToken cancellation, bool nativeBody = false) =>
        Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".obj" => ScienceModelDecoder.Decode(file, cancellation),
            ".cmod" => nativeBody ? CmodMeshDecoder.DecodeNativeBody(root, file, cancellation) : CmodMeshDecoder.Decode(root, file, cancellation),
            ".cms" => DisplacedSphereDecoder.Decode(file, cancellation),
            ".3ds" => StudioMeshDecoder.Decode(root, file, cancellation),
            ".glb" or ".gltf" => PackedModelDecoder.Decode(root, file, cancellation),
            _ => throw new NotSupportedException("The add-on mesh needs OBJ, CMOD, CMS, 3DS, glTF or GLB.")
        };

    private IReadOnlyList<DecodedSatellite> ReadScience(AddonSourcePath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        string pack = Path.Combine(root, "pack.json");
        IReadOnlyList<ScienceSatellite> tracks = AddonDependencies.FileExists(pack)
            ? PackedSatelliteCatalog.Load(root, message => _report("Packed satellites: " + message))
            : location.Explicit is not null || AddonDependencies.FileExists(catalog)
                ? ScienceSatelliteCatalog.Load(catalog, root) : [];
        return Array.AsReadOnly(tracks.Select(track =>
        {
            cancellation.ThrowIfCancellationRequested();
            return new DecodedSatellite(track, Path.GetExtension(track.MeshPath).ToLowerInvariant() is ".glb" or ".gltf"
                ? PackedModelDecoder.Decode(root, track.MeshPath, cancellation) : ScienceModelDecoder.Decode(track.MeshPath, cancellation));
        }).ToArray());
    }

    private string? FindDedicatedMission(AddonSourcePath location)
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
        return path is null ? null : Path.GetFullPath(path);
    }

    private DecodedMission DecodeDedicatedMission(string fullPath, SpiceCatalogResult catalog, CancellationToken cancellation)
    {
        string root = Path.GetDirectoryName(fullPath)!;
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

    private DecodedTimeline? ReadTimeline(AddonSourcePath location, CancellationToken cancellation)
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

    private DecodedTerrain? ReadTerrain(AddonSourcePath location, CancellationToken cancellation)
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

    private string[] SscSources(string catalog, string root)
    {
        string directory = Path.Combine(root, "overrides");
        return AddonDependencies.DirectoryExists(directory) ? [catalog, .. AddonDependencies.ListFiles(directory, "*.ssc")] : [catalog];
    }
    private IReadOnlyDictionary<string, byte[]> ReadMissionTextures(SampledMission mission,
        Dictionary<string, byte[]> cache, ref int total, CancellationToken cancellation)
    {
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var geometry = mission.Geometry ?? [];
        var paths = geometry.Select(span => span.Shape.GlobeTexturePath)
            .Concat(geometry.SelectMany(span => span.Shape.ParticleEmitters ?? []).Select(emitter => emitter.TexturePath));
        foreach (string path in paths.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var data))
            {
                data = ReadMissionTexture(path, ref total);
                cache.Add(path, data);
            }
            textures.Add(path, data);
        }
        return textures;
    }

    private IReadOnlyDictionary<string, byte[]> ReadMissionTiles(SampledMission mission,
        Dictionary<string, byte[]> cache, ref int total, CancellationToken cancellation)
    {
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var maps in (mission.Geometry ?? []).Select(span => span.Shape.SurfaceMaps).OfType<BodySurfaceMaps>())
        foreach (string path in (maps.Color?.Tiles.Values ?? []).Concat(maps.Normal?.Tiles.Values ?? []).Distinct())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var data))
            {
                const int maxTileBytes = 16 * 1024 * 1024, maxTotalBytes = 192 * 1024 * 1024;
                using var file = AddonDependencies.OpenRead(path, maxTileBytes);
                if (file.Length > maxTotalBytes - total)
                    throw new InvalidDataException("Mission surface tiles exceed the catalogue size limit.");
                data = new byte[(int)file.Length];
                file.ReadExactly(data);
                string format = Path.GetExtension(path).Equals(".dxt5nm", StringComparison.OrdinalIgnoreCase)
                    ? Path.ChangeExtension(path, ".dds") : path;
                ModelTextureFormats.Validate(format, data);
                total += data.Length;
                cache.Add(path, data);
            }
            resources[path] = data;
        }
        return resources;
    }

    private byte[] ReadMissionTexture(string path, ref int total)
    {
        const int maximumTextureBytes = 8 * 1024 * 1024;
        using FileStream source = AddonDependencies.OpenRead(path, maximumTextureBytes);
        if (source.Length > maximumTextureBytes || total + source.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("Local textures exceed the per-file or catalogue size limit.");
        var data = new byte[(int)source.Length];
        source.ReadExactly(data);
        AddonDiagnostics.Read(AddonStage.Texture, path, () =>
        {
            ModelTextureFormats.Validate(path, data);
            return true;
        });
        total += data.Length;
        return data;
    }

}
