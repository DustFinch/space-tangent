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

    [MirModelFact]
    [Trait("Category", "ExternalData")]
    public void OriginalMirPreservesEmissionInSourceAndExport()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_SPACECRAFT")!;
        var source = CmodMeshDecoder.Decode(root, Path.Combine(root, "models", "mir.cmod"));
        var emissions = source.Surfaces.Select(surface => surface.Material.Emission).Where(color => color.Length > 0).ToArray();
        Assert.NotEmpty(emissions);
        var highlights = source.Surfaces.Select(surface => (surface.Material.Specular, surface.Material.SpecularPower)).ToArray();
        Assert.Contains(highlights, highlight => Math.Abs(highlight.Specular.X - .8) < 1e-6 && highlight.SpecularPower == 55);
        Assert.Contains(emissions, color => Math.Abs(color.X - .02955684) < 1e-6 &&
            Math.Abs(color.Y - .28314874) < 1e-6 && Math.Abs(color.Z - .28314874) < 1e-6);

        var probe = StudioMeshDecoder.Decode(root, Path.Combine(root, "models", "galileo-probe.3ds"));
        Assert.Contains(probe.Surfaces, surface => surface.Material.Specular == new SpaceTangent.Contracts.Vector3d(1, 1, 1) &&
            Math.Abs(surface.Material.SpecularPower - Math.Pow(2, 2.5)) < 1e-9);
        string export = Path.GetTempFileName();
        try
        {
            using (var output = File.Create(export))
                PackedModelWriter.Write(source, output);
            var packed = PackedModelDecoder.Decode(export);
            Assert.Equal(emissions, packed.Surfaces.Select(surface => surface.Material.Emission).Where(color => color.Length > 0).ToArray());
            Assert.Equal(highlights, packed.Surfaces.Select(surface => (surface.Material.Specular, surface.Material.SpecularPower)).ToArray());
        }
        finally
        {
            File.Delete(export);
        }
    }

    private sealed class MirModelFactAttribute : FactAttribute
    {
        public MirModelFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_SPACECRAFT")))
                Skip = "Set SPACETANGENT_STANDARD_SPACECRAFT to the original spacecraft catalogue.";
        }
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
