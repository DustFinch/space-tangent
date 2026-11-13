// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static DecodedDeepSky ReadDeepSky(string root, CancellationToken cancellation)
    {
        var index = DeepSkyIndex.Load(root, cancellation);
        var sprites = new Dictionary<ObjectId, IReadOnlyList<DecodedModelSprite>>();
        var models = new Dictionary<string, IReadOnlyList<DecodedModelSprite>>(StringComparer.Ordinal);
        long count = 0;
        foreach (var body in index.Objects)
        {
            if (body.Kind != DeepSkyKind.Nebula || body.Mesh is not { Length: > 0 } name ||
                !Path.GetExtension(name).Equals(".cmod", StringComparison.OrdinalIgnoreCase))
                continue;

            cancellation.ThrowIfCancellationRequested();
            try
            {
                string path = DeepSkyIndex.ResolveMesh(root, body);
                if (!models.TryGetValue(path, out var model))
                    models.Add(path, model = CmodMeshDecoder.DecodeSprites(path, cancellation));
                sprites.Add(body.Id, model);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
            {
                global::Godot.GD.Print($"Deep sky: {body.Designation}: {error.Message}");
                continue;
            }
            count += sprites[body.Id].Count;
            if (count > 1_000_000)
                throw new InvalidDataException("The deep-sky catalogue exceeds one million model sprites.");
        }
        return new(index, sprites);
    }
}
