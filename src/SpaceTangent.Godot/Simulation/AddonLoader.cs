// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Godot.Simulation;

internal sealed record AddonGeneration(AddonLoader.DecodedAddOns Decoded, IReadOnlyList<PreparedSatellite> Satellites,
    PreparedMission? Mission, PreparedMission? Timeline, PreparedProbe? Probe, LunarTerrainAsset? Terrain);

internal static class AddonLoader
{
    internal sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);
    internal sealed record DecodedMission(SpiceSpacecraft Track, DecodedScienceModel Model,
        IReadOnlyList<SpiceSensor> Sensors, SaturnSurface? Surface);
    internal sealed record DecodedTimeline(SscSampledSpacecraft Track, DecodedScienceModel Model, HuygensMission? Probe,
        DecodedScienceModel? ProbeModel);
    internal sealed record DecodedTerrain(SpicePolynomialBodyFrame Frame, DecodedLunarTerrain Model);

    internal sealed record Cached<T>(LocalDataPath Path, AddonDependencies Dependencies, T Value)
    {
        internal static Cached<T> Load(Cached<T>? previous, LocalDataPath path,
            Func<LocalDataPath, CancellationToken, T> read, CancellationToken cancellation)
        {
            if (previous?.Path == path && previous.Dependencies.IsCurrent(cancellation))
                return previous;
            var (value, dependencies) = AddonDependencies.Capture(() => read(path, cancellation), cancellation);
            return new(path, dependencies, value);
        }
    }

    internal sealed record DecodedAddOns(SimulationWorld World, Cached<IReadOnlyList<DecodedSatellite>> Science,
        Cached<DecodedMission?> Mission, Cached<DecodedTimeline?> Timeline, Cached<DecodedTerrain?> Terrain);

    // Godot path resolution runs on the main thread. File probes, hashing and decoding run on the worker.
    internal static Task<DecodedAddOns> Load(De440Ephemeris ephemeris, DecodedAddOns? previous, CancellationToken cancellation)
    {
        var science = LocalDataPaths.Candidates("SPACETANGENT_SCIENCE_ROOT", "science", directory: true);
        var mission = LocalDataPaths.Candidates("SPACETANGENT_MISSION_CATALOG", "mission/cassini.json");
        var timeline = LocalDataPaths.Candidates("SPACETANGENT_TIMELINE_ROOT", "timeline", directory: true);
        var terrain = LocalDataPaths.Candidates("SPACETANGENT_TERRAIN_ROOT", "terrain", directory: true);
        return Task.Run(() =>
        {
            var satellites = Cached<IReadOnlyList<DecodedSatellite>>.Load(previous?.Science, science, ReadScience, cancellation);
            var spice = Cached<DecodedMission?>.Load(previous?.Mission, mission, ReadMission, cancellation);
            var sampled = Cached<DecodedTimeline?>.Load(previous?.Timeline, timeline, ReadTimeline, cancellation);
            var relief = Cached<DecodedTerrain?>.Load(previous?.Terrain, terrain, ReadTerrain, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (previous is not null && ReferenceEquals(previous.Science, satellites) && ReferenceEquals(previous.Mission, spice) &&
                ReferenceEquals(previous.Timeline, sampled) && ReferenceEquals(previous.Terrain, relief))
                return previous;
            var world = new SimulationWorld(ephemeris, satellites.Value.Select(asset => asset.Track), spice.Value?.Track,
                sampled.Value?.Track, sampled.Value?.Probe, relief.Value?.Frame);
            return new(world, satellites, spice, sampled, relief);
        }, cancellation);
    }

    internal static AddonGeneration Publish(DecodedAddOns decoded, AddonGeneration? previous)
    {
        if (previous is not null && ReferenceEquals(previous.Decoded, decoded))
            return previous;

        IReadOnlyList<PreparedSatellite> satellites = previous is not null && ReferenceEquals(previous.Decoded.Science, decoded.Science)
            ? previous.Satellites : Array.AsReadOnly(decoded.Science.Value.Select(item => new PreparedSatellite(
                item.Track, ScienceMeshLoader.Publish(item.Model), Scale(item.Model))).ToArray());
        PreparedMission? mission = previous is not null && ReferenceEquals(previous.Decoded.Mission, decoded.Mission)
            ? previous.Mission : decoded.Mission.Value is { } geometry
                ? new(decoded.World.MissionId!.Value, ScienceMeshLoader.Publish(geometry.Model), Scale(geometry.Model),
                    SimulationViewport.CreateFrusta(geometry.Sensors), geometry.Surface, geometry.Sensors)
                : null;
        PreparedMission? timeline = previous?.Timeline;
        PreparedProbe? probe = previous?.Probe;
        if (!ReferenceEquals(previous?.Decoded.Timeline, decoded.Timeline))
        {
            var track = decoded.Timeline.Value;
            timeline = track is null ? null : new(track.Track.Id, ScienceMeshLoader.Publish(track.Model),
                Scale(track.Model), null, null, []);
            probe = track?.ProbeModel is { } probeModel && track.Probe is { } probeMission
                ? new(probeMission.Track, ScienceMeshLoader.Publish(probeModel), (float)(24 / probeModel.Diameter)) : null;
        }
        LunarTerrainAsset? terrain = previous is not null && ReferenceEquals(previous.Decoded.Terrain, decoded.Terrain)
            ? previous.Terrain : decoded.Terrain.Value is { } relief ? LunarTerrainAsset.Publish(relief.Model) : null;
        return new(decoded, satellites, mission, timeline, probe, terrain);

        static float Scale(DecodedScienceModel model) => (float)(18 / model.Diameter);
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
        var track = SscSampledCatalog.Load(sources, root, "Sol/Saturn/Cassini", cancellation);
        var geometry = StudioMeshDecoder.Decode(track.MeshPath, cancellation);
        string sat083 = Path.Combine(root, "sat083.bsp"), pck = Path.Combine(root, "pck00011.tpc");
        if (!AddonDependencies.FileExists(sat083) || !AddonDependencies.FileExists(pck))
            return new(track, geometry, null, null);

        var frame = new SpicePolynomialBodyFrame(pck, 606);
        var probe = SscHuygensTrack.Load(sources, root, track, frame, cancellation);
        return new(track, geometry, new(probe, new(sat083), frame),
            StudioMeshDecoder.Decode(probe.MeshPath, cancellation, centerOnBounds: true));
    }

    private static DecodedTerrain? ReadTerrain(LocalDataPath location, CancellationToken cancellation)
    {
        string root = location.Resolve();
        string path = Path.Combine(root, "terrain-moon.bin");
        if (!AddonDependencies.FileExists(path))
            return null;
        var frame = new SpicePolynomialBodyFrame(Path.Combine(root, "pck00011.tpc"), 301);
        return new(frame, DecodedLunarTerrain.Load(path, frame.RadiiKilometers.X, cancellation));
    }

    private static string[] SscSources(string catalog, string root)
    {
        string directory = Path.Combine(root, "overrides");
        return AddonDependencies.DirectoryExists(directory) ? [catalog, .. AddonDependencies.ListFiles(directory, "*.ssc")] : [catalog];
    }
}
