// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static IReadOnlyDictionary<string, byte[]> ReadParticleTextures(SampledMission mission,
        Dictionary<string, byte[]> cache, ref int total, CancellationToken cancellation)
    {
        const int maximumTextureBytes = 8 * 1024 * 1024;
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in (mission.Geometry ?? []).SelectMany(span => span.Shape.ParticleEmitters ?? [])
            .Select(emitter => emitter.TexturePath).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var data))
            {
                using FileStream source = AddonDependencies.OpenRead(path, maximumTextureBytes);
                if (source.Length > maximumTextureBytes || total + source.Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Particle textures exceed the per-file or catalogue size limit.");
                data = new byte[(int)source.Length];
                source.ReadExactly(data);
                total += data.Length;
                cache.Add(path, data);
            }
            textures.Add(path, data);
        }
        return textures;
    }

    private static void PublishParticleTextures(IReadOnlyDictionary<string, byte[]> source, Dictionary<string, Texture2D> textures)
    {
        foreach (var (path, data) in source)
        {
            if (textures.ContainsKey(path))
                continue;
            textures.Add(path, AddonDiagnostics.Read(AddonStage.Texture, path, () =>
            {
                using var image = new Image();
                Error status = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? image.LoadPngFromBuffer(data) : image.LoadJpgFromBuffer(data);
                if (status != Error.Ok || image.IsEmpty() || image.GetWidth() > 4096 || image.GetHeight() > 4096)
                    throw new InvalidDataException("A particle texture must be a readable PNG or JPEG no larger than 4096×4096.");
                return ImageTexture.CreateFromImage(image);
            }));
        }
    }
}
