// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PlanetTextures(IReadOnlyDictionary<ObjectId, Texture2D> Surfaces, IReadOnlyDictionary<ObjectId, Texture2D> Rings)
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
        return new(surfaces, rings);
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
