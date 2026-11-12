// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyModelFormatsTests
{
    [ModelFormatFact]
    [Trait("Category", "ExternalData")]
    public void ReadsTheArchivedAsciiMaterialAndOriginalDisplacedSphere()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_MODEL_FORMATS")!;
        string models = Path.Combine(root, "models");
        var ascii = CmodMeshDecoder.Decode(root, Path.Combine(models, "ship.cmod"));
        DecodedSurface triangle = Assert.Single(ascii.Surfaces);
        Assert.Equal(new Vector3d[] { default, new(1, 0, 0), new(0, 1, 0) }, triangle.Positions.ToArray());
        Assert.Equal("panel.png", triangle.Material.Texture);
        Assert.Equal(File.ReadAllBytes(Path.Combine(models, "panel.png")), ascii.Textures["panel.png"].ToArray());

        var sphere = DisplacedSphereDecoder.Decode(Path.Combine(models, "roughsphere.cms"));
        var surface = Assert.Single(sphere.Surfaces);
        Assert.Equal(9800 * 3, surface.Positions.Length);
        var points = surface.Positions.ToArray();
        double x = points.Max(position => position.X) - points.Min(position => position.X);
        double y = points.Max(position => position.Y) - points.Min(position => position.Y);
        double z = points.Max(position => position.Z) - points.Min(position => position.Z);
        Assert.InRange(Math.Abs(x / z - 169.5284977555275 / 172.5), 0, 1e-6);
        Assert.InRange(Math.Abs(y / z - 169.75343272089958 / 172.5), 0, 1e-6);
    }

    private sealed class ModelFormatFactAttribute : FactAttribute
    {
        public ModelFormatFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_MODEL_FORMATS")))
                Skip = "Set the archived model formats fixture.";
        }
    }
}
