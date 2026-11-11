// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class CmodMeshDecoderTests
{
    [SscModelFact]
    [Trait("Category", "ExternalData")]
    public void OriginalStationContainsTexturedTriangleSurfaces()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!;
        var model = CmodMeshDecoder.Decode(root, Path.Combine(root, "models", "iss.cmod"));
        Assert.InRange(model.Surfaces.Count, 2, 128);
        Assert.InRange(model.Textures.Count, 8, 13);
        Assert.True(model.Surfaces.Sum(surface => surface.Positions.Length) > 100_000);
        Assert.All(model.Surfaces, surface =>
        {
            Assert.Equal(surface.Positions.Length, surface.Normals.Length);
            Assert.Equal(surface.Positions.Length * 2, surface.TextureCoordinates.Length);
        });
    }

    private sealed class SscModelFactAttribute : FactAttribute
    {
        public SscModelFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")))
                Skip = "Set SPACETANGENT_SSC_ADDON_PACK to the verified body add-on pack.";
        }
    }
}
