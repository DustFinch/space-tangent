// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Addons;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private const string TileShader = """
        shader_type canvas_item;
        render_mode unshaded;
        #include "res://Shaders/surface_tiles.gdshaderinc"
        uniform sampler2D reference_color : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
        uniform sampler2D reference_normal : filter_linear_mipmap_anisotropic, repeat_disable;
        uniform vec2 query_uv;
        uniform vec2 query_dx;
        uniform vec2 query_dy;
        uniform vec2 reference_uv;
        uniform vec2 reference_dx;
        uniform vec2 reference_dy;
        uniform bool normal_mode = false;
        void fragment() {
            vec4 color, normal;
            bool found_color, found_normal;
            native_tile_samples(query_uv, query_dx, query_dy, color, normal, found_color, found_normal);
            if (UV.x < 0.5) {
                COLOR = normal_mode ? (found_normal ? vec4(normal.ag, 0.0, 1.0) : vec4(1.0, 0.0, 1.0, 1.0)) :
                    (found_color ? vec4(color.rgb, 1.0) : vec4(1.0, 0.0, 1.0, 1.0));
            } else {
                vec4 sampled_color = textureGrad(reference_color, reference_uv, reference_dx, reference_dy);
                vec4 sampled_normal = textureGrad(reference_normal, reference_uv, reference_dx, reference_dy);
                COLOR = normal_mode ? vec4(sampled_normal.ag, 0.0, 1.0) : vec4(sampled_color.rgb, 1.0);
            }
        }
        """;

    private async Task ExerciseTileSampling(SolarSystemScene scene, string directory)
    {
        string? root = System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_TILES");
        string? fixture = System.Environment.GetEnvironmentVariable("SPACETANGENT_TILE_FIXTURE");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fixture) || DisplayServer.GetName() == "headless")
            return;
        var tiles = scene.Renderer(0).LocalSurfaceTiles(PlanetCatalogue.MarsId);
        if (tiles is not { Color.Count: 170, Normal.Count: 170 } ||
            !ReferenceEquals(scene.Renderer(0).MarsTerrain?.Tiles, tiles))
            throw new InvalidDataException("The original Mars maps must be installed in both the globe and terrain materials.");

        BodySurfaceMaps original = LocalSurfaceMaps.Load(root).Single(map => map.Name == "Mars");
        using var input = File.OpenRead(Path.Combine(fixture, "reproduction", "nametemplate-clean-first", "reference.json"));
        using var expected = JsonDocument.Parse(input);
        var cases = expected.RootElement.GetProperty("regions").EnumerateArray().Where(item =>
            item.GetProperty("levels").GetInt32() == 4 && item.GetProperty("border").GetSingle() == 0).ToArray();
        using var shader = new Shader { Code = TileShader };
        using var material = new ShaderMaterial { Shader = shader };
        var viewport = new SubViewport { Size = new(128, 64), Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(viewport);
        viewport.AddChild(new ColorRect { Size = new(128, 64), Material = material });
        int count = 0;
        float maximumDifference = 0;
        try
        {
            foreach (uint mask in new[] { uint.MaxValue, 2u })
            {
                SurfaceTilePyramid Filter(SurfaceTilePyramid map) => map with
                    { Tiles = map.Tiles.Where(pair => (mask & (1u << pair.Key.Level)) != 0).ToDictionary() };
                SurfaceTileTexture color = mask == uint.MaxValue ? tiles.Color! : SurfaceTileTexture.Create(Filter(original.Color!))!;
                SurfaceTileTexture normals = mask == uint.MaxValue ? tiles.Normal! : SurfaceTileTexture.Create(Filter(original.Normal!))!;
                color.Bind(material, normal: false);
                normals.Bind(material, normal: true);
                for (int level = mask == uint.MaxValue ? 0 : 1; level < 4; level++)
                {
                    int columns = 2 << level, rows = 1 << level;
                    foreach (var point in new[] { new Vector2I(0, 0), new(columns - 1, rows - 1), new(columns / 3, rows / 3) })
                    {
                        var item = cases.First(row => row.GetProperty("level").GetInt32() == level &&
                            row.GetProperty("column").GetInt32() == point.X && row.GetProperty("row").GetInt32() == point.Y &&
                            row.GetProperty("mask").GetUInt32() == mask);
                        string reference = item.GetProperty("resource").GetString() ?? throw new InvalidDataException("Missing reference tile.");
                        string[] address = Path.GetFileNameWithoutExtension(reference).Split('_');
                        int sourceLevel = int.Parse(address[1]), north = int.Parse(address[3]);
                        var key = new SurfaceTileAddress(sourceLevel, int.Parse(address[2]), (1 << sourceLevel) - 1 - north);
                        using var colorTexture = Reference(original.Color!.Tiles[key]);
                        using var normalTexture = Reference(original.Normal!.Tiles[key]);
                        material.SetShaderParameter("reference_color", colorTexture);
                        material.SetShaderParameter("reference_normal", normalTexture);
                        var bounds = item.GetProperty("rect");
                        float u0 = bounds[0].GetSingle(), v0 = bounds[1].GetSingle();
                        float du = bounds[2].GetSingle() - u0, dv = bounds[3].GetSingle() - v0;
                        float derivative = MathF.Pow(2, -level + .25f) / (2 * original.Color.EffectiveTileSize);
                        material.SetShaderParameter("query_uv", new Vector2((point.X + .43f) / columns, 1 - (point.Y + .37f) / rows));
                        material.SetShaderParameter("query_dx", new Vector2(derivative, 0));
                        material.SetShaderParameter("query_dy", new Vector2(0, 2 * derivative));
                        material.SetShaderParameter("reference_uv", new Vector2(u0 + .43f * du, 1 - (v0 + .37f * dv)));
                        material.SetShaderParameter("reference_dx", new Vector2(derivative * columns * du, 0));
                        material.SetShaderParameter("reference_dy", new Vector2(0, 2 * derivative * rows * dv));
                        foreach (bool normal in new[] { false, true })
                        {
                            material.SetShaderParameter("normal_mode", normal);
                            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                            using var image = viewport.GetTexture().GetImage();
                            Color actual = image.GetPixel(32, 32), direct = image.GetPixel(96, 32);
                            float difference = Math.Max(Math.Abs(actual.R - direct.R),
                                Math.Max(Math.Abs(actual.G - direct.G), Math.Abs(actual.B - direct.B)));
                            if (difference > 1.01f / 255)
                                throw new InvalidDataException($"GPU page mismatch at {level}/{point.X}/{point.Y} normal={normal} mask={mask}: {actual} versus {direct}.");
                            maximumDifference = Math.Max(maximumDifference, difference);
                            count++;
                        }
                    }
                }
            }
        }
        finally
        {
            viewport.QueueFree();
        }
        File.WriteAllText(Path.Combine(directory, "mars-tiles.json"), JsonSerializer.Serialize(new { count, maximumDifference }));
        GD.Print($"Mars local tile sampling: {count} GPU color and normal comparisons, including parent fallback; maximum difference {maximumDifference:R}.");

        static ImageTexture Reference(string path)
        {
            using var image = new Image();
            if (image.LoadDdsFromBuffer(File.ReadAllBytes(path)) != Error.Ok)
                throw new InvalidDataException($"Cannot load independent reference tile {path}.");
            return ImageTexture.CreateFromImage(image);
        }
    }
}
