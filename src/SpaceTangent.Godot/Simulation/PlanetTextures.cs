// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PlanetTextures(IReadOnlyDictionary<ObjectId, Texture2D> Surfaces, IReadOnlyDictionary<ObjectId, Texture2D> Rings, Texture2D? VenusClouds)
{
    internal IReadOnlyDictionary<char, Texture2D> StellarSurfaces { get; init; } = new Dictionary<char, Texture2D>();
    internal Texture2DArray? GroundSurfaceMaps { get; init; }

    private enum MapKind { Surface, Ring, Star, VenusClouds }
    private readonly record struct MapRequest(MapKind Kind, ObjectId Body, char Star, string Directory, string File);

    /// <summary>
    /// Decodes the planetary maps on a worker thread and creates their textures on the calling thread. Hi-res maps
    /// take seconds to decode, which used to freeze the window during startup.
    /// </summary>
    internal static async Task<PlanetTextures> LoadAsync(CancellationToken cancellation = default, Func<bool>? canPublish = null,
        Func<Task>? yieldFrame = null)
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
        Image?[] groundImages = new Image?[PlanetCatalogue.Planets.Count];
        var createdTextures = new List<Resource>();
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
                using var prepare = RuntimeProfile.Measure("textures.ground-prepare");
                for (int index = 0; index < PlanetCatalogue.Planets.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var planet = PlanetCatalogue.Planets[index];
                    int source = requests.FindIndex(item => item.Kind == MapKind.Surface && item.Body == planet.Id);
                    Image? image = source >= 0 ? images[source] : null;
                    groundImages[index] = PrepareGroundMap(image);
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
            var slice = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < requests.Count; index++)
            {
                if (images[index] is not { } image)
                    continue;
                Texture2D created = RuntimeProfile.Run("textures.publish-one", () => ImageTexture.CreateFromImage(image));
                createdTextures.Add(created);
                MapRequest request = requests[index];
                switch (request.Kind)
                {
                    case MapKind.Surface: surfaces.Add(request.Body, created); break;
                    case MapKind.Ring: rings.Add(request.Body, created); break;
                    case MapKind.Star: stars.Add(request.Star, created); break;
                    default: venus = created; break;
                }
                if (yieldFrame is not null && slice.Elapsed.TotalMilliseconds >= 4)
                {
                    await yieldFrame();
                    CheckOwner(cancellation, canPublish);
                    slice.Restart();
                }
            }
            CheckOwner(cancellation, canPublish);
            var maps = new global::Godot.Collections.Array<Image>();
            foreach (Image? image in groundImages) maps.Add(image!);
            var ground = new Texture2DArray();
            createdTextures.Add(ground);
            if (ground.CreateFromImages(maps) != Error.Ok)
                throw new InvalidDataException("The renderer rejected the prepared ground maps.");
            var result = new PlanetTextures(surfaces, rings, venus) { StellarSurfaces = stars, GroundSurfaceMaps = ground };
            published = true;
            return result;
        }
        finally
        {
            foreach (Image? image in images)
                image?.Dispose();
            foreach (Image? image in groundImages)
                image?.Dispose();
            if (!published)
                foreach (Resource texture in createdTextures)
                    texture.Dispose();
        }
    }

    internal Texture2DArray? CreateGroundSurfaceArray() => GroundSurfaceMaps;

    private static Image PrepareGroundMap(Image? source)
    {
        const int width = 512, height = 256;
        var map = source is null ? Image.CreateEmpty(width, height, false, Image.Format.Rgb8)
            : Image.CreateFromData(source.GetWidth(), source.GetHeight(), source.HasMipmaps(), source.GetFormat(), source.GetData());
        try
        {
            if (map.IsCompressed() && map.Decompress() != Error.Ok)
                throw new InvalidDataException("Could not decompress the ground map.");
            map.ClearMipmaps();
            map.Convert(Image.Format.Rgb8);
            if (map.GetWidth() != width || map.GetHeight() != height) map.Resize(width, height, Image.Interpolation.Lanczos);
            if (map.GenerateMipmaps() != Error.Ok) throw new InvalidDataException("Could not prepare ground-map mipmaps.");
            return map;
        }
        catch { map.Dispose(); throw; }
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

    internal static async Task<Texture2D?> ReadAsync(string root, string file, CancellationToken cancellation, Func<bool> canPublish)
    {
        using Image? image = await Task.Run(() => Decode(root, file), cancellation);
        CheckOwner(cancellation, canPublish);
        return image is null ? null : RuntimeProfile.Run("textures.publish-one", () => ImageTexture.CreateFromImage(image));
    }

    private static void CheckOwner(CancellationToken cancellation, Func<bool>? canPublish)
    {
        cancellation.ThrowIfCancellationRequested();
        if (canPublish is not null && !canPublish())
            throw new OperationCanceledException("The texture owner is no longer active.", cancellation);
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
