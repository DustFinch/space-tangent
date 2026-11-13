// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PlanetTextures(IReadOnlyDictionary<ObjectId, Texture2D> Surfaces, IReadOnlyDictionary<ObjectId, Texture2D> Rings, Texture2D? VenusClouds)
{
    internal IReadOnlyDictionary<char, Texture2D> StellarSurfaces { get; init; } = new Dictionary<char, Texture2D>();

    internal static PlanetTextures Load()
    {
        string root = LocalDataPaths.Find("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        var surfaces = new Dictionary<ObjectId, Texture2D>();
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
            if (planet.NaifId != 399 && Read(root, planet.Texture) is { } image)
                surfaces.Add(planet.Id, image);
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
            if (body.Texture is { } texture && Read(root, texture) is { } image)
                surfaces.Add(body.Id, image);
        string ringsRoot = System.Environment.GetEnvironmentVariable("SPACETANGENT_RING_TEXTURES") is { Length: > 0 } overrideRoot
            ? Path.GetFullPath(overrideRoot) : root;
        var rings = new Dictionary<ObjectId, Texture2D>();
        foreach (PlanetRingProfile profile in PlanetRingRendering.Profiles)
            if (Read(ringsRoot, profile.Texture) is { } ring)
                rings.Add(profile.Id, ring);
        var stars = new Dictionary<char, Texture2D>();
        foreach (char surface in "obafgkm")
            if (Read(root, $"star-{surface}.jpg") is { } image)
                stars.Add(surface, image);
        return new(surfaces, rings, Read(root, "venus-clouds.jpg")) { StellarSurfaces = stars };
    }

    internal Texture2DArray? CreateGroundSurfaceArray()
    {
        const int width = 512, height = 256;
        var maps = new global::Godot.Collections.Array<Image>();
        try
        {
            foreach (var planet in PlanetCatalogue.Planets)
            {
                var map = Surfaces.TryGetValue(planet.Id, out var texture)
                    ? texture.GetImage() : Image.CreateEmpty(width, height, false, Image.Format.Rgb8);
                maps.Add(map);
                if (map.IsCompressed() && map.Decompress() != Error.Ok)
                    throw new InvalidDataException($"Could not decompress the ground map for {planet.Name}.");
                map.ClearMipmaps();
                map.Convert(Image.Format.Rgb8);
                if (map.GetWidth() != width || map.GetHeight() != height)
                    map.Resize(width, height, Image.Interpolation.Lanczos);
                map.GenerateMipmaps();
            }
            var array = new Texture2DArray();
            if (array.CreateFromImages(maps) == Error.Ok)
                return array;
            array.Dispose();
            return null;
        }
        finally
        {
            foreach (var map in maps)
                map.Dispose();
        }
    }

    internal Texture2D? CreateGroundRingAtlas()
    {
        if (Rings.Count == 0)
            return null;
        const int width = 2048;
        var profiles = PlanetRingRendering.Profiles;
        using var atlas = Image.CreateEmpty(width, profiles.Length, false, Image.Format.Rgba8);
        for (int row = 0; row < profiles.Length; row++)
        {
            if (!Rings.TryGetValue(profiles[row].Id, out var texture))
                continue;
            using var source = texture.GetImage();
            if (source.IsCompressed() && source.Decompress() != Error.Ok)
                continue;
            int sourceWidth = source.GetWidth();
            int center = source.GetHeight() / 2;
            for (int x = 0; x < width; x++)
            {
                float position = (float)x / (width - 1) * (sourceWidth - 1);
                int start = (int)position;
                var color = source.GetPixel(start, center).Lerp(
                    source.GetPixel(Math.Min(start + 1, sourceWidth - 1), center), position - start);
                atlas.SetPixel(x, row, color);
            }
        }
        return ImageTexture.CreateFromImage(atlas);
    }

    internal static Texture2D? Read(string root, string file)
    {
        string path = Path.Combine(root, file);
        if (!File.Exists(path))
            return null;
        using Image image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
            throw new InvalidDataException($"Could not decode the planetary texture {file}.");
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
