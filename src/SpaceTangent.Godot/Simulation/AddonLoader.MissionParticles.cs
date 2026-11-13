// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static IReadOnlyDictionary<string, byte[]> ReadMissionTextures(SampledMission mission,
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

    private static IReadOnlyDictionary<string, byte[]> ReadMissionTiles(SampledMission mission,
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

    private static ShaderMaterial? PublishTiledMissionGlobe(BodySurfaceMaps maps,
        IReadOnlyDictionary<string, byte[]> bytes, Texture2D? fallback)
    {
        var tiles = new BodyTileTextures(SurfaceTileTexture.Create(maps.Color, bytes), SurfaceTileTexture.Create(maps.Normal, bytes));
        if (tiles.Color is null && tiles.Normal is null)
            return null;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader") };
        material.SetShaderParameter("globe_color", new Color(.8f, .8f, .8f));
        material.SetShaderParameter("prime_meridian_offset", .25f);
        material.SetShaderParameter("has_globe_map", fallback is not null);
        if (fallback is not null)
            material.SetShaderParameter("globe_map", fallback);
        tiles.Bind(material);
        return material;
    }

    private static void PublishMissionTextures(IReadOnlyDictionary<string, byte[]> source, Dictionary<string, Texture2D> textures)
    {
        foreach (var (path, data) in source)
        {
            if (textures.ContainsKey(path))
                continue;
            textures.Add(path, PublishMissionTexture(path, data));
        }
    }

    private static byte[] ReadMissionTexture(string path, ref int total)
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

    private static Texture2D PublishMissionTexture(string path, byte[] data) =>
        AddonDiagnostics.Read(AddonStage.Texture, path, () =>
        {
            using var image = new Image();
            Error status = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(data),
                ".dds" => image.LoadDdsFromBuffer(data),
                _ => image.LoadPngFromBuffer(data)
            };
            if (status != Error.Ok || image.IsEmpty() || image.GetWidth() > 8192 || image.GetHeight() > 8192)
                throw new InvalidDataException("A local texture must be a readable PNG, JPEG or DDS no larger than 8192×8192.");
            if (!image.IsCompressed() && !image.HasMipmaps())
                image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        });
}
