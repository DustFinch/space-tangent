// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static ShaderMaterial? PublishTiledMissionGlobe(BodySurfaceMaps maps,
        IReadOnlyDictionary<string, byte[]> bytes, Texture2D? fallback)
    {
        var tiles = new BodyTileTextures(SurfaceTileTexture.Create(maps.Color, bytes), SurfaceTileTexture.Create(maps.Normal, bytes));
        if (tiles.Color is null && tiles.Normal is null)
            return null;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader") };
        material.SetShaderParameter("globe_color", new Color(.8f, .8f, .8f));
        material.SetShaderParameter("prime_meridian_offset", .25f);
        material.SetShaderParameter("has_globe_map", fallback is not null);
        if (fallback is not null)
            material.SetShaderParameter("globe_map", fallback);
        tiles.Bind(material);
        return material;
    }

    private static void PublishMissionTextures(IReadOnlyDictionary<string, byte[]> source, Dictionary<string, Texture2D> textures)
    {
        foreach (var (path, data) in source)
        {
            if (textures.ContainsKey(path))
                continue;
            textures.Add(path, PublishMissionTexture(path, data));
        }
    }

    private static Texture2D PublishMissionTexture(string path, byte[] data) =>
        AddonDiagnostics.Read(AddonStage.Texture, path, () =>
        {
            using var image = new Image();
            Error status = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(data),
                ".dds" => image.LoadDdsFromBuffer(data),
                _ => image.LoadPngFromBuffer(data)
            };
            if (status != Error.Ok || image.IsEmpty() || image.GetWidth() > 8192 || image.GetHeight() > 8192)
                throw new InvalidDataException("A local texture must be a readable PNG, JPEG or DDS no larger than 8192×8192.");
            if (!image.IsCompressed() && !image.HasMipmaps())
                image.GenerateMipmaps();
            return ImageTexture.CreateFromImage(image);
        });
}
