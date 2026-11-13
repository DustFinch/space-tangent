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

    [ArchivedModelsFact]
    [Trait("Category", "ExternalData")]
    public void WritesOriginalJasonAndSorceAsSelfContainedGlb()
    {
        string input = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        var output = Directory.CreateTempSubdirectory("space-tangent-glb-");
        try
        {
            foreach (string name in new[] { "jason", "sorce" })
            {
                string file = Directory.GetFiles(input, name + ".obj", SearchOption.AllDirectories).Single();
                var source = ScienceModelDecoder.Decode(file);
                string target = Path.Combine(output.FullName, name + ".glb");
                using (var stream = File.Create(target))
                    PackedModelWriter.Write(source, stream);
                var restored = PackedModelDecoder.Decode(target);
                Assert.Equal(source.Surfaces.Count, restored.Surfaces.Count);
                Assert.InRange(Math.Abs(source.Diameter - restored.Diameter), 0, 1e-4);
                Assert.Equal(source.Textures.Count, restored.Textures.Count);
                foreach (var (left, right) in source.Surfaces.Zip(restored.Surfaces))
                {
                    Assert.Equal(left.Positions.Length, right.Positions.Length);
                    Assert.Equal(left.TextureCoordinates.ToArray(), right.TextureCoordinates.ToArray());
                    for (int corner = 0; corner < left.Positions.Length; corner++)
                    {
                        Assert.True((left.Positions.Span[corner] - right.Positions.Span[corner]).Length < 1e-4);
                        var normal = left.Normals.Span[corner];
                        if (normal.Length > 0)
                            Assert.True((normal / normal.Length - right.Normals.Span[corner]).Length < 1e-6);
                    }
                    Assert.InRange((left.Material.Color - right.Material.Color).Length, 0, 1e-7);
                    Assert.Equal(left.Material.Opacity, right.Material.Opacity);
                    if (left.Material.Texture is { } texture)
                        Assert.True(source.Textures[texture].Span.SequenceEqual(restored.Textures[right.Material.Texture!].Span));
                }
            }
        }
        finally
        {
            output.Delete(recursive: true);
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
            Assert.Equal(new SpaceTangent.Contracts.Vector3d(.1, .2, .3), surface.Material.Emission);

            File.WriteAllText(model, json.Replace("../triangle%20data.bin", "data:application/octet-stream;base64," + Convert.ToBase64String(bytes)));
            Assert.Equal(surface.Positions.ToArray(), Assert.Single(PackedModelDecoder.Decode(model).Surfaces).Positions.ToArray());
            File.WriteAllText(model, json.Replace("../triangle%20data.bin", "%2e%2e/%2e%2e/outside.bin"));
            Assert.Throws<InvalidDataException>(() => PackedModelDecoder.Decode(root, model));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PointSpritesSurviveAnIndexedGltfAndGlbRoundTrip()
    {
        using var bytes = new MemoryStream();
        using var data = new BinaryWriter(bytes);
        foreach (float coordinate in new float[] { -2, 0, 0, 2, 0, 0 })
            data.Write(coordinate);
        data.Write(new byte[] { 255, 0, 0, 255, 0, 128, 255, 64 });
        data.Write(2f);
        data.Write(4f);
        data.Write(new byte[] { 1, 0 });

        string json = """
            {"asset":{"version":"2.0"},"nodes":[{"mesh":0,"translation":[4,0,0]}],
             "meshes":[{"primitives":[{"mode":0,"material":0,"indices":3,
                 "attributes":{"POSITION":0,"COLOR_0":1,"_POINTSIZE":2}}]}],
             "materials":[{"alphaMode":"BLEND","pbrMetallicRoughness":{"baseColorFactor":[1,1,1,0.5]}}],
             "buffers":[{"uri":"data:application/octet-stream;base64,DATA","byteLength":42}],
             "bufferViews":[{"buffer":0,"byteLength":24},{"buffer":0,"byteOffset":24,"byteLength":8},
                            {"buffer":0,"byteOffset":32,"byteLength":8},{"buffer":0,"byteOffset":40,"byteLength":2}],
             "accessors":[{"bufferView":0,"componentType":5126,"type":"VEC3","count":2},
                          {"bufferView":1,"componentType":5121,"type":"VEC4","normalized":true,"count":2},
                          {"bufferView":2,"componentType":5126,"type":"SCALAR","count":2},
                          {"bufferView":3,"componentType":5121,"type":"SCALAR","count":2}]}
            """;
        var root = Directory.CreateTempSubdirectory("space-tangent-gltf-points-");
        try
        {
            string source = Path.Combine(root.FullName, "points.gltf");
            File.WriteAllText(source, json.Replace("base64,DATA", "base64," + Convert.ToBase64String(bytes.ToArray())));
            var model = PackedModelDecoder.Decode(source);
            Assert.Empty(model.Surfaces);
            Assert.Equal(new(6, 0, 0), model.Sprites[0].Position);
            Assert.Equal(new(0, 128 / 255.0, 1), model.Sprites[0].Color);
            Assert.Equal(32 / 255.0, model.Sprites[0].Opacity);
            Assert.Equal(4, model.Sprites[0].Size);

            string packed = Path.Combine(root.FullName, "points.glb");
            using (var output = File.Create(packed))
                PackedModelWriter.Write(model, output);
            var normalized = PackedModelDecoder.DecodeSprites(root.FullName, packed);
            Assert.Collection(normalized, first =>
            {
                Assert.Equal(new(1, 0, 0), first.Position);
                Assert.Equal(2, first.Size);
                Assert.Equal(32 / 255.0, first.Opacity, 7);
            }, second =>
            {
                Assert.Equal(new(-1, 0, 0), second.Position);
                Assert.Equal(1, second.Size);
                Assert.Equal(.5, second.Opacity);
            });

            File.WriteAllText(source, json.Replace("base64,DATA", "base64," + Convert.ToBase64String(bytes.ToArray()))
                .Replace("\"SCALAR\",\"count\":2},", "\"SCALAR\",\"count\":1},"));
            Assert.Throws<InvalidDataException>(() => PackedModelDecoder.Decode(source));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
