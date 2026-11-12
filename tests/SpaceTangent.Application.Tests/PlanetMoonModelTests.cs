// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class PlanetMoonModelTests
{
    [MoonModelsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalMarsMoonMeshesAndPackedModelsPreserveRawGeometryAndMaterialInputs()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_SOURCE")!;
        string packed = Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_PACKED")!;
        foreach (var (name, triangles) in new[] { ("phobos", 49_152), ("deimos", 5_040) })
        {
            var original = CmodMeshDecoder.DecodeNativeBody(source, Path.Combine(source, name + ".cmod"));
            var bundle = PackedModelDecoder.Decode(Path.Combine(packed, name + ".glb"));
            var a = Assert.Single(original.Surfaces);
            var b = Assert.Single(bundle.Surfaces);
            Assert.Equal(triangles * 3, a.Positions.Length);
            Assert.Equal(a.Positions.Length, b.Positions.Length);
            Assert.Equal(a.Positions.ToArray(), b.Positions.ToArray());
            Assert.Equal(a.TextureCoordinates.ToArray(), b.TextureCoordinates.ToArray());
            Assert.InRange(Math.Abs(original.Diameter - bundle.Diameter), 0, 1e-10);
            if (name == "phobos")
            {
                Assert.Equal("phobos-normals.dds", a.Material.NormalTexture);
                Assert.Equal(File.ReadAllBytes(Path.Combine(source, "phobos-normals.dds")),
                    original.Textures[a.Material.NormalTexture!].ToArray());
                Assert.Null(b.Material.NormalTexture);
            }
            else
            {
                Assert.Equal("deimos.dds", a.Material.Texture);
                Assert.Equal(original.Textures[a.Material.Texture!].ToArray(), bundle.Textures[b.Material.Texture!].ToArray());
            }
        }
    }

    private sealed class MoonModelsFactAttribute : FactAttribute
    {
        public MoonModelsFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_SOURCE") is null ||
                Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_PACKED") is null)
                Skip = "Set the original Mars moon model and packed model directories.";
        }
    }
}
