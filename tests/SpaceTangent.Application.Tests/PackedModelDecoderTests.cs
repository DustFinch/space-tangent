// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class PackedModelDecoderTests
{
    [ArchivedModelsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalPackedGlbPreservesTriangleCountsBoundsAndTextures()
    {
        string original = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        string packed = Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")!;
        foreach (string name in new[] { "jason", "sorce" })
        {
            string file = Directory.GetFiles(original, name + ".obj", SearchOption.AllDirectories).Single();
            var source = ScienceModelDecoder.Decode(file);
            var compiled = PackedModelDecoder.Decode(Path.Combine(packed, "models", name + ".glb"));
            Assert.Equal(source.Surfaces.Sum(surface => surface.Positions.Length), compiled.Surfaces.Sum(surface => surface.Positions.Length));
            Assert.InRange(Math.Abs(source.Diameter - compiled.Diameter), 0, .01);
            Assert.Equal(source.Textures.Count, compiled.Textures.Count);
            Assert.All(compiled.Textures.Values, texture => Assert.True(texture.Length > 33));
            Assert.All(compiled.Surfaces, surface =>
            {
                Assert.Equal(surface.Positions.Length, surface.Normals.Length);
                Assert.Equal(surface.Positions.Length * 2, surface.TextureCoordinates.Length);
                Assert.True(surface.Material.Texture is null || compiled.Textures.ContainsKey(surface.Material.Texture));
            });
        }
    }

    [Fact]
    public void RejectsATruncatedBinaryModel()
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, [0x67, 0x6c, 0x54, 0x46, 2, 0, 0, 0, 128, 0, 0, 0]);
            Assert.Throws<InvalidDataException>(() => PackedModelDecoder.Decode(file));
        }
        finally { File.Delete(file); }
    }

    private sealed class ArchivedModelsFactAttribute : FactAttribute
    {
        public ArchivedModelsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set the original and packed science installations.";
        }
    }
}
