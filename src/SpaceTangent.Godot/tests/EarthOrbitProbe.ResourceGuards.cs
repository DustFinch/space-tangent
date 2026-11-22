// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseResourceGuards(SolarSystemScene scene, string directory)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROFILE") != "1")
            throw new InvalidOperationException("Resource guard acceptance requires profiling enabled before startup.");
        for (int i = 0; i < 4100; i++) RuntimeProfile.Record("bounded-reference", i);
        using var samples = JsonDocument.Parse(JsonSerializer.Serialize(RuntimeProfile.Snapshot()));
        var bounded = samples.RootElement.GetProperty("bounded-reference");
        Require(bounded.GetProperty("Count").GetInt32() == 4096 && bounded.GetProperty("Median").GetDouble() == 2052 &&
            bounded.GetProperty("Max").GetDouble() == 4099, "Profiling must retain only its latest 4096 samples and report their median and maximum.");

        string root = Path.Combine(directory, "landscapes"), id = "bounded";
        string folder = Path.Combine(root, id); Directory.CreateDirectory(folder);
        string ini = Path.Combine(folder, "landscape.ini"), png = Path.Combine(folder, "ground.png");
        const string definition = "[landscape]\nname=Bounded\ntype=spherical\nmaptex=ground.png\n[location]\nplanet=Earth\n";
        Reject(() => LandscapeResources.Read(root, "../outside", default));
        File.WriteAllText(ini, definition.Replace("Earth", "Moon", StringComparison.Ordinal));
        Reject(() => LandscapeResources.Read(root, id, default));
        using (var large = File.Create(ini)) large.SetLength(65537);
        Reject(() => LandscapeResources.Read(root, id, default));
        File.WriteAllText(ini, definition);
        Reject(() => LandscapeResources.Read(root, id, default));
        File.WriteAllBytes(png, [1]); Reject(() => LandscapeResources.Read(root, id, default));
        File.WriteAllBytes(png, new byte[24]); Reject(() => LandscapeResources.Read(root, id, default));
        byte[] header = new byte[24];
        BinaryPrimitives.WriteUInt64BigEndian(header, 0x89504E470D0A1A0A);
        "IHDR"u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), 8193);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), 1);
        File.WriteAllBytes(png, header); Reject(() => LandscapeResources.Read(root, id, default));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16), 4);
        File.WriteAllBytes(png, header); Reject(() => LandscapeResources.Read(root, id, default));
        using (var image = Image.CreateEmpty(4, 2, false, Image.Format.Rgba8))
        {
            image.Fill(new(.2f, .3f, .4f));
            Require(image.SavePng(png) == Error.Ok, "The recovery panorama must save.");
        }
        var panorama = LandscapeResources.Read(root, id, default);
        Require(panorama.Ground.Pixels.Width == 4 && panorama.Ground.Pixels.Height == 2 && panorama.Lights is null,
            "The landscape decoder must recover after invalid inputs.");

        Require(SurfaceTileTexture.Create(null) is null, "An absent local tile map cannot allocate GPU resources.");
        var tileMap = new SurfaceTilePyramid(1, 4, 0, true, true, false,
            new Dictionary<SurfaceTileAddress, string> { [new(0, 0, 0)] = png });
        var tiles = SurfaceTileTexture.Create(tileMap) ?? throw new InvalidOperationException("A valid uncompressed PNG tile must upload.");
        Require(tiles.Count == 1, "The authored tile map must contain exactly one page.");
        Reject(() => SurfaceTileTexture.Create(tileMap, new Dictionary<string, byte[]> { [png] = header }));
        Reject(() => ScienceMeshLoader.Publish(new DecodedScienceModel([], new Dictionary<string, ReadOnlyMemory<byte>>(), 0),
            Path.Combine(directory, "empty.obj")));

        var media = new ScriptMediaPlayer(scene.Renderer(0));
        try
        {
            media.Present(folder, new ScriptMediaRequest.Overlay("ground.png", .05, .5, .5, 4, 2, 1));
            await ToSignal(GetTree().CreateTimer(.15), SceneTreeTimer.SignalName.Timeout);
            Require(!media.Active, "An expired script image must release its texture and canvas control.");
            string webp = Path.Combine(folder, "ground.webp");
            using var image = Image.LoadFromFile(png);
            File.WriteAllBytes(webp, image.SaveWebpToBuffer());
            media.Present(folder, new ScriptMediaRequest.Overlay("ground.webp", .05, .5, .5, 4, 2, 1));
            await ToSignal(GetTree().CreateTimer(.15), SceneTreeTimer.SignalName.Timeout);
            Require(!media.Active, "WebP overlays must expire through the same resource lifecycle.");
        }
        finally { media.Clear(); }
        using (var cache = new GalacticTextureCache())
        {
            int published = 0;
            cache.Published += () => published++;
            var centre = SpaceTangent.Core.Observing.GalacticLight.SunKiloparsecs;
            if (cache.HasDust || cache.Request(centre, 0).Texture is not null)
                throw new InvalidOperationException("A new analytic galactic map must bake asynchronously without a dust grid.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (published == 0)
            {
                if (clock.Elapsed.TotalSeconds > 90) throw new TimeoutException("The analytic galactic map did not publish.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            var ready = cache.Request(centre, 0);
            if (ready.Texture is null || ready.Texture.GetWidth() != 512 || ready.Texture.GetHeight() != 256 ||
                !ReferenceEquals(ready.Texture, cache.Request(centre + new Vector3d(.001, 0, 0), 0).Texture))
                throw new InvalidOperationException("The quantized galactic cache must reuse its actual completed GPU texture.");
            using var pixels = ready.Texture.GetImage();
            Color sample = pixels.GetPixel(256, 128);
            if (!float.IsFinite(sample.R) || sample.R < 0 || !float.IsFinite(sample.G) || !float.IsFinite(sample.B))
                throw new InvalidOperationException("A completed analytic light map must contain finite nonnegative radiance.");
            cache.Request(centre + new Vector3d(1, 0, 0), 0);
            cache.Dispose();
            await ToSignal(GetTree().CreateTimer(.15), SceneTreeTimer.SignalName.Timeout);
            if (published != 1 || cache.Request(centre, 0).Texture is not null)
                throw new InvalidOperationException("Disposing the cache must cancel pending work and prevent subsequent publication.");
        }
        GD.Print("Resource guards GPU: bounded timings, malformed panoramas, tile decoding, empty meshes and overlay expiry passed.");

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Malformed renderer resources were accepted.");
        }
    }
}
