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

    [Fact]
    public void TextSceneComposesTransformsAndUsesLocalResources()
    {
        string root = Directory.CreateTempSubdirectory("SpaceTangent-gltf-").FullName;
        try
        {
            string models = Directory.CreateDirectory(Path.Combine(root, "models")).FullName;
            string model = Path.Combine(models, "scene.gltf");
            var bytes = new byte[36];
            for (int i = 0; i < 9; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), i is 3 or 7 ? 1 : 0);
            File.WriteAllBytes(Path.Combine(root, "triangle data.bin"), bytes);
            string json = """
                {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],
                 "nodes":[{"translation":[1,2,3],"children":[1]},{"mesh":0,"scale":[-2,3,1]}],
                 "buffers":[{"uri":"../triangle%20data.bin","byteLength":36}],
                 "bufferViews":[{"buffer":0,"byteLength":36}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
                 "meshes":[{"primitives":[{"attributes":{"POSITION":0},"material":0}]}],
                 "materials":[{"alphaMode":"MASK","alphaCutoff":0.3,"emissiveFactor":[0.1,0.2,0.3],
                     "pbrMetallicRoughness":{"baseColorFactor":[1,1,1,0.7]}}]}
                """;
            File.WriteAllText(model, json);
            var surface = Assert.Single(PackedModelDecoder.Decode(root, model).Surfaces);
            Assert.Equal(new SpaceTangent.Contracts.Vector3d(1, 2, 3), surface.Positions.Span[0]);
            Assert.Equal(new SpaceTangent.Contracts.Vector3d(-1, 2, 3), surface.Positions.Span[1]);
            Assert.Equal(new SpaceTangent.Contracts.Vector3d(1, 5, 3), surface.Positions.Span[2]);
            Assert.All(surface.Normals.ToArray(), normal => Assert.Equal(new SpaceTangent.Contracts.Vector3d(0, 0, 1), normal));
            Assert.Equal(.7, surface.Material.Opacity);
            Assert.Equal(.3, surface.Material.AlphaCutoff);
            Assert.True(surface.Material.Emission.Length > .5);

            File.WriteAllText(model, json.Replace("../triangle%20data.bin", "data:application/octet-stream;base64," + Convert.ToBase64String(bytes)));
            Assert.Equal(surface.Positions.ToArray(), Assert.Single(PackedModelDecoder.Decode(model).Surfaces).Positions.ToArray());
            File.WriteAllText(model, json.Replace("../triangle%20data.bin", "%2e%2e/%2e%2e/outside.bin"));
            Assert.Throws<InvalidDataException>(() => PackedModelDecoder.Decode(root, model));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [ShuttleFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedShuttleMatchesItsExternalGltfAtEveryCorner()
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("SPACETANGENT_GLTF_SHUTTLE")!, "models");
        var glb = PackedModelDecoder.Decode(Path.Combine(directory, "shuttle-original.glb"));
        var gltf = PackedModelDecoder.Decode(Path.Combine(directory, "shuttle-external.gltf"));
        Assert.Equal(42, glb.Surfaces.Count);
        Assert.Equal(glb.Surfaces.Count, gltf.Surfaces.Count);
        Assert.Equal(glb.Diameter, gltf.Diameter);
        Assert.Equal(11, glb.Textures.Count);
        foreach (var (name, bytes) in glb.Textures)
            Assert.True(bytes.Span.SequenceEqual(gltf.Textures[name].Span), name);
        foreach (var (left, right) in glb.Surfaces.Zip(gltf.Surfaces))
        {
            Assert.Equal(left.Material, right.Material);
            Assert.True(left.Positions.Span.SequenceEqual(right.Positions.Span));
            Assert.True(left.Normals.Span.SequenceEqual(right.Normals.Span));
            Assert.True(left.TextureCoordinates.Span.SequenceEqual(right.TextureCoordinates.Span));
        }
    }

    private sealed class ShuttleFactAttribute : FactAttribute
    {
        public ShuttleFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_GLTF_SHUTTLE")))
                Skip = "Set the archived shuttle and derived text glTF installation.";
        }
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
