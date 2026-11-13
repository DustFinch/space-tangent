// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PlanetTextures(IReadOnlyDictionary<ObjectId, Texture2D> Surfaces, IReadOnlyDictionary<ObjectId, Texture2D> Rings, Texture2D? VenusClouds)
{
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
        return new(surfaces, rings, Read(root, "venus-clouds.jpg"));
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
