// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static IReadOnlyDictionary<string, byte[]> ReadMissionTextures(SampledMission mission,
        Dictionary<string, byte[]> cache, ref int total, CancellationToken cancellation)
    {
        const int maximumTextureBytes = 8 * 1024 * 1024;
        var textures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var geometry = mission.Geometry ?? [];
        var paths = geometry.Select(span => span.Shape.GlobeTexturePath)
            .Concat(geometry.SelectMany(span => span.Shape.ParticleEmitters ?? []).Select(emitter => emitter.TexturePath));
        foreach (string path in paths.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var data))
            {
                using FileStream source = AddonDependencies.OpenRead(path, maximumTextureBytes);
                if (source.Length > maximumTextureBytes || total + source.Length > 64 * 1024 * 1024)
                    throw new InvalidDataException("Mission textures exceed the per-file or catalogue size limit.");
                data = new byte[(int)source.Length];
                source.ReadExactly(data);
                AddonDiagnostics.Read(AddonStage.Texture, path, () =>
                {
                    ModelTextureFormats.Validate(path, data);
                    return true;
                });
                total += data.Length;
                cache.Add(path, data);
            }
            textures.Add(path, data);
        }
        return textures;
    }

    private static void PublishMissionTextures(IReadOnlyDictionary<string, byte[]> source, Dictionary<string, Texture2D> textures)
    {
        foreach (var (path, data) in source)
        {
            if (textures.ContainsKey(path))
                continue;
            textures.Add(path, AddonDiagnostics.Read(AddonStage.Texture, path, () =>
            {
                using var image = new Image();
                Error status = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(data),
                    ".dds" => image.LoadDdsFromBuffer(data),
                    _ => image.LoadPngFromBuffer(data)
                };
                if (status != Error.Ok || image.IsEmpty() || image.GetWidth() > 8192 || image.GetHeight() > 8192)
                    throw new InvalidDataException("A mission texture must be a readable PNG, JPEG or DDS no larger than 8192×8192.");
                if (!image.IsCompressed() && !image.HasMipmaps())
                    image.GenerateMipmaps();
                return ImageTexture.CreateFromImage(image);
            }));
        }
    }
}
