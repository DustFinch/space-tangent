// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PlanetTextures(IReadOnlyDictionary<ObjectId, Texture2D> Surfaces, IReadOnlyDictionary<ObjectId, Texture2D> Rings, Texture2D? VenusClouds)
{
    internal IReadOnlyDictionary<char, Texture2D> StellarSurfaces { get; init; } = new Dictionary<char, Texture2D>();

    private enum MapKind { Surface, Ring, Star, VenusClouds }
    private readonly record struct MapRequest(MapKind Kind, ObjectId Body, char Star, string Directory, string File);

    /// <summary>
    /// Decodes the planetary maps on a worker thread and creates their textures on the calling thread. Hi-res maps
    /// take seconds to decode, which used to freeze the window during startup.
    /// </summary>
    internal static async Task<PlanetTextures> LoadAsync(CancellationToken cancellation = default, Func<bool>? canPublish = null)
    {
        string root = LocalDataPaths.Find("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        string ringsRoot = System.Environment.GetEnvironmentVariable("SPACETANGENT_RING_TEXTURES") is { Length: > 0 } overrideRoot
            ? Path.GetFullPath(overrideRoot) : root;
        var requests = new List<MapRequest>();
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
            if (planet.NaifId != 399)
                requests.Add(new(MapKind.Surface, planet.Id, default, root, planet.Texture));
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
            if (body.Texture is { } texture)
                requests.Add(new(MapKind.Surface, body.Id, default, root, texture));
        foreach (PlanetRingProfile profile in PlanetRingRendering.Profiles)
            requests.Add(new(MapKind.Ring, profile.Id, default, ringsRoot, profile.Texture));
        foreach (char surface in "obafgkm")
            requests.Add(new(MapKind.Star, default, surface, root, $"star-{surface}.jpg"));
        requests.Add(new(MapKind.VenusClouds, default, default, root, "venus-clouds.jpg"));

        Image?[] images = new Image?[requests.Count];
        var createdTextures = new List<Texture2D>();
        bool published = false;
        try
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < requests.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    images[index] = Decode(requests[index].Directory, requests[index].File);
                }
            }, cancellation);
            // This continuation runs on Godot's main thread. A closed/replaced scene must never publish GPU resources.
            cancellation.ThrowIfCancellationRequested();
            if (canPublish is not null && !canPublish())
                throw new OperationCanceledException("The texture owner is no longer active.", cancellation);
            var surfaces = new Dictionary<ObjectId, Texture2D>();
            var rings = new Dictionary<ObjectId, Texture2D>();
            var stars = new Dictionary<char, Texture2D>();
            Texture2D? venus = null;
            for (int index = 0; index < requests.Count; index++)
            {
                if (images[index] is not { } image)
                    continue;
                Texture2D created = ImageTexture.CreateFromImage(image);
                createdTextures.Add(created);
                MapRequest request = requests[index];
                switch (request.Kind)
                {
                    case MapKind.Surface: surfaces.Add(request.Body, created); break;
                    case MapKind.Ring: rings.Add(request.Body, created); break;
                    case MapKind.Star: stars.Add(request.Star, created); break;
                    default: venus = created; break;
                }
            }
            var result = new PlanetTextures(surfaces, rings, venus) { StellarSurfaces = stars };
            published = true;
            return result;
        }
        finally
        {
            foreach (Image? image in images)
                image?.Dispose();
            if (!published)
                foreach (Texture2D texture in createdTextures)
                    texture.Dispose();
        }
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
        using Image? image = Decode(root, file);
        return image is null ? null : ImageTexture.CreateFromImage(image);
    }

    /// <summary>Decodes a map with its mipmaps. Safe on a worker thread; it creates no rendering resources.</summary>
    private static Image? Decode(string root, string file)
    {
        string path = Path.Combine(root, file);
        if (!File.Exists(path))
            return null;
        Image image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
        {
            image?.Dispose();
            throw new InvalidDataException($"Could not decode the planetary texture {file}.");
        }
        try
        {
            if (image.GenerateMipmaps() != Error.Ok)
                throw new InvalidDataException($"Could not generate mipmaps for {file}.");
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
