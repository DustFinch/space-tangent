// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal sealed record BodyTileTextures(SurfaceTileTexture? Color, SurfaceTileTexture? Normal)
{
    internal static IReadOnlyDictionary<ObjectId, BodyTileTextures> Load()
    {
        string root = LocalDataPaths.Find("SPACETANGENT_SURFACE_TILES", "surface-tiles", directory: true);
        var identifiers = PlanetCatalogue.Planets.Select(body => (body.Name, body.Id))
            .Concat(NaturalBodyCatalogue.Bodies.Select(body => (body.Name, body.Id)))
            .Where(body => body.Id != SolarSystemSample.EarthId)
            .ToDictionary(body => body.Name, body => body.Id, StringComparer.OrdinalIgnoreCase);
        var maps = new Dictionary<ObjectId, BodyTileTextures>();
        foreach (BodySurfaceMaps source in LocalSurfaceMaps.Load(root))
        {
            if (!identifiers.TryGetValue(source.Name, out var id))
                continue;
            var color = SurfaceTileTexture.Create(source.Color);
            var normal = SurfaceTileTexture.Create(source.Normal);
            if (color is not null || normal is not null)
                maps.Add(id, new(color, normal));
        }
        return maps;
    }

    internal void Bind(ShaderMaterial material)
    {
        Color?.Bind(material, normal: false);
        Normal?.Bind(material, normal: true);
    }
}

internal sealed class SurfaceTileTexture
{
    private const int MaximumPools = 4, LayersPerPool = 256, MaximumPageProbes = 8;
    private readonly SurfaceTilePyramid _map;
    private readonly ImageTexture _pages;
    private readonly global::Godot.Collections.Array<Texture2DArray> _pools;
    private readonly int _mask;

    private SurfaceTileTexture(SurfaceTilePyramid map, ImageTexture pages,
        global::Godot.Collections.Array<Texture2DArray> pools, int mask)
    {
        _map = map;
        _pages = pages;
        _pools = pools;
        _mask = mask;
    }

    internal int Count => _map.Tiles.Count;

    internal void Bind(ShaderMaterial material, bool normal)
    {
        string kind = normal ? "native_normal_" : "native_base_";
        material.SetShaderParameter(kind + "enabled", true);
        material.SetShaderParameter(kind + "tiles", _pools);
        material.SetShaderParameter(kind + "pages", _pages);
        material.SetShaderParameter(kind + "levels", _map.LevelCount);
        material.SetShaderParameter(kind + "size", _map.EffectiveTileSize);
        material.SetShaderParameter(kind + "border", _map.Border);
        material.SetShaderParameter(kind + "mask", _mask);
        material.SetShaderParameter(kind + "axes", new Vector2I(_map.HasColumns ? 1 : 0, _map.HasRows ? 1 : 0));
        material.SetShaderParameter("native_layers_per_pool", LayersPerPool);
        material.SetShaderParameter("native_page_probe_limit", MaximumPageProbes);
        if (normal)
            material.SetShaderParameter("native_normal_encoding", _map.PackedNormals ? 1 : 2);
    }

    internal static SurfaceTileTexture? Create(SurfaceTilePyramid? map, IReadOnlyDictionary<string, byte[]>? decoded = null)
    {
        if (map is null || map.Tiles.Count == 0)
            return null;
        var images = new Dictionary<string, Image>(StringComparer.Ordinal);
        var pools = new global::Godot.Collections.Array<Texture2DArray>();
        try
        {
            foreach (string path in map.Tiles.Values.Distinct(StringComparer.Ordinal))
            {
                bool dds = Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(path).Equals(".dxt5nm", StringComparison.OrdinalIgnoreCase);
                var image = new Image();
                images.Add(path, image);
                Error status = decoded is null
                    ? dds ? image.LoadDdsFromBuffer(File.ReadAllBytes(path)) : image.Load(path)
                    : dds ? image.LoadDdsFromBuffer(decoded[path])
                    : Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? image.LoadJpgFromBuffer(decoded[path])
                    : image.LoadPngFromBuffer(decoded[path]);
                if (status != Error.Ok || image.IsEmpty())
                    throw new InvalidDataException($"Could not decode the local surface tile {Path.GetFileName(path)}.");
                if (!image.IsCompressed() && !image.HasMipmaps())
                    image.GenerateMipmaps();
            }
            var pageOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in images.GroupBy(pair => (pair.Value.GetWidth(), pair.Value.GetHeight(),
                         pair.Value.GetFormat(), pair.Value.HasMipmaps())))
            foreach (var chunk in group.Chunk(LayersPerPool))
            {
                if (pools.Count == MaximumPools)
                    throw new InvalidDataException("The surface tile map exceeds the texture array capacity.");
                var source = new global::Godot.Collections.Array<Image>(chunk.Select(pair => pair.Value));
                var texture = new Texture2DArray();
                if (texture.CreateFromImages(source) != Error.Ok)
                {
                    texture.Dispose();
                    throw new InvalidDataException("Could not upload the surface tiles.");
                }
                int pool = pools.Count;
                pools.Add(texture);
                for (int layer = 0; layer < chunk.Length; layer++)
                    pageOf.Add(chunk[layer].Key, pool * LayersPerPool + layer + 1);
            }

            int capacity = 1;
            while (capacity < map.Tiles.Count * 4)
                capacity *= 2;
            float[]? table;
            while ((table = PackPages(map.Tiles, pageOf, capacity)) is null)
            {
                if (capacity >= 1 << 20)
                    throw new InvalidDataException("Could not build the local tile page table.");
                capacity *= 2;
            }
            byte[] bytes = new byte[table.Length * sizeof(float)];
            Buffer.BlockCopy(table, 0, bytes, 0, bytes.Length);
            int width = Math.Min(256, capacity);
            using var pageImage = Image.CreateFromData(width, capacity / width, false, Image.Format.Rgbaf, bytes);
            var pages = ImageTexture.CreateFromImage(pageImage);
            while (pools.Count < MaximumPools)
                pools.Add(pools[0]);
            return new(map, pages, pools, capacity - 1);
        }
        catch
        {
            foreach (var pool in pools.Distinct())
                pool.Dispose();
            pools.Clear();
            throw;
        }
        finally
        {
            foreach (var image in images.Values)
                image.Dispose();
        }
    }

    private static float[]? PackPages(IReadOnlyDictionary<SurfaceTileAddress, string> tiles,
        IReadOnlyDictionary<string, int> pageOf, int capacity)
    {
        var table = new float[capacity * 4];
        int mask = capacity - 1;
        foreach (var (address, file) in tiles)
        {
            uint hash = unchecked((uint)address.Column * 1664525u ^ (uint)address.Row * 1013904223u ^ (uint)address.Level * 2654435761u);
            int slot = (int)(hash & (uint)mask), probes = 0;
            while (table[4 * slot] != 0 && probes < MaximumPageProbes)
            {
                slot = (slot + 1) & mask;
                probes++;
            }
            if (probes == MaximumPageProbes)
                return null;
            table[4 * slot] = address.Level + 1;
            table[4 * slot + 1] = address.Column;
            table[4 * slot + 2] = address.Row;
            table[4 * slot + 3] = pageOf[file];
        }
        return table;
    }
}
