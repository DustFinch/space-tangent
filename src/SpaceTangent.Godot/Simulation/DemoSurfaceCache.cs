// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class DemoSurfaceCache
{
    internal sealed record Maps(Texture2D Albedo, Texture2D Normal, Texture2D? Clouds, Texture2D? Emission, Texture2D? Rings);
    private sealed record Baked(byte[] Albedo, byte[] Normal, byte[]? Clouds, byte[]? Emission, byte[]? Rings);

    private const int Width = 512, Height = 256, RingWidth = 256;
    private readonly Dictionary<ObjectId, Maps> _maps = new();
    private readonly HashSet<ObjectId> _failed = [];
    private Task<Baked>? _job;
    private ObjectId _pending;

    internal bool PublishReady()
    {
        if (_job is { IsCompleted: true } completed)
        {
            _job = null;
            if (completed.IsCompletedSuccessfully)
            {
                Baked raw = completed.Result;
                _maps.Add(_pending, new(
                    Texture(raw.Albedo, Image.Format.Rgba8)!, Texture(raw.Normal, Image.Format.Rgb8)!,
                    Texture(raw.Clouds, Image.Format.R8), Texture(raw.Emission, Image.Format.Rgb8),
                    Texture(raw.Rings, Image.Format.Rgba8, RingWidth, 1)));
            }
            else
            {
                _failed.Add(_pending);
                GD.PushWarning($"Procedural maps for {_pending.Value}: {completed.Exception?.GetBaseException().Message}");
            }
            return true;
        }
        return false;
    }

    internal Maps? Get(DemoWorldBody body)
    {
        if (_maps.TryGetValue(body.Id, out Maps? maps))
            return maps;
        if (_job is null && !_failed.Contains(body.Id))
        {
            _pending = body.Id;
            _job = Task.Run(() => Bake(body));
        }
        return null;
    }

    private static Baked Bake(DemoWorldBody body)
    {
        var surface = DemoWorlds.CreateSurface(body)!;
        var (albedo, normal) = surface.BakeSurface(Width, Height, maximumThreads: 2);
        return new(albedo, normal,
            body.CloudTopKilometers > 0 ? surface.BakeClouds(Width, Height, maximumThreads: 2) : null,
            surface.Kind == ProceduralWorldKind.Volcanic ? surface.BakeEmission(Width, Height, maximumThreads: 2) : null,
            body.RingOuterKilometers > 0 ? surface.BakeRings(RingWidth) : null);
    }

    private static Texture2D? Texture(byte[]? bytes, Image.Format format, int width = Width, int height = Height)
    {
        if (bytes is null)
            return null;
        using var image = Image.CreateFromData(width, height, false, format, bytes);
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
