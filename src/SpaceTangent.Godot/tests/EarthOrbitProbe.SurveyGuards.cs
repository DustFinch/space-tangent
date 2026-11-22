// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSurveyGuards(string directory)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string root = Path.Combine(directory, "surveys"); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "missing-properties"));
        Directory.CreateDirectory(Path.Combine(root, "broken-properties"));
        File.WriteAllText(Path.Combine(root, "broken-properties", "properties"), "hips_order=invalid\n");
        File.WriteAllText(Path.Combine(directory, "textures.json"), "{broken");
        using var tiles = new SkySurveyTiles(root, directory);
        tiles.SetOffline(true);
        if (tiles.Photographs.Count != 0 || tiles.Find("broken-properties") is not null)
            throw new InvalidOperationException("Invalid local metadata must not publish partial surveys or photographs.");
        foreach (string name in new[] { "tiny.png", "wide.png", "bad-header.png", "large.png" })
        {
            string path = Path.Combine(directory, name);
            if (name == "large.png") { using var file = File.Create(path); file.SetLength(16L * 1024 * 1024 + 1); }
            else if (name == "wide.png")
            {
                using var image = Image.CreateEmpty(4097, 2, false, Image.Format.Rgba8);
                if (image.SavePng(path) != Error.Ok) throw new IOException("Could not save the survey dimension witness.");
            }
            else File.WriteAllBytes(path, new byte[name == "tiny.png" ? 1 : 24]);
            if (tiles.Texture(path, null) is not null || tiles.Texture(path, null) is not null)
                throw new InvalidOperationException("Malformed survey photographs must be rejected and their repeated lookup throttled.");
        }
        for (int i = 0; i < 4097; i++)
            if (tiles.Texture(Path.Combine(directory, $"missing-{i}.png"), null) is not null)
                throw new InvalidOperationException("Missing lookup rollover must not create a texture.");
        byte[] png;
        using (var image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8))
        {
            image.Fill(new(.1f, .8f, .2f)); png = image.SavePngToBuffer();
        }
        Texture2D? first = null;
        string firstPath = Path.Combine(directory, "bounded-0.png");
        for (int i = 0; i < 221; i++)
        {
            string path = Path.Combine(directory, $"bounded-{i}.png"); File.WriteAllBytes(path, png);
            var texture = tiles.Texture(path, null) ?? throw new InvalidOperationException("A valid bounded survey image must decode.");
            first ??= texture;
        }
        var again = tiles.Texture(firstPath, null);
        if (again is null || ReferenceEquals(first, again) || !ReferenceEquals(again, tiles.Texture(firstPath, null)))
            throw new InvalidOperationException("The 220-image LRU bound must evict and then stably recache the oldest texture.");
        using var pixels = again.GetImage();
        if (pixels.GetPixel(0, 0).G < .7f)
            throw new InvalidOperationException("Cache eviction must preserve the decoded photograph's colours.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print("Survey guards GPU: invalid metadata and images, 4096 missing lookups, 220-image LRU eviction and colour-preserving recovery passed.");
    }
}
