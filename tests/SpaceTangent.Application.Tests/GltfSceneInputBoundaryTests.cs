// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class GltfSceneInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-gltf-input-");
    private static JsonObject Triangle()
    {
        byte[] bytes = new byte[36];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), 1);
        var json = JsonNode.Parse("""
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],
             "buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteLength":36}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}]}
            """)!.AsObject();
        json["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);
        return json;
    }

    [Theory]
    [InlineData("matrix")] [InlineData("matrix-length")] [InlineData("quaternion")]
    [InlineData("translation-length")] [InlineData("repeated-root")] [InlineData("cycle")]
    [InlineData("negative-node")] [InlineData("far-node")] [InlineData("buffer-missing")]
    [InlineData("buffer-negative")] [InlineData("buffer-size")] [InlineData("empty-uri")]
    [InlineData("uri-format")] [InlineData("uri-encoding")] [InlineData("uri-network")]
    [InlineData("uri-traversal")] [InlineData("uri-control")]
    public void InvalidSceneGraphsAndResourcesAreRejectedAtTheMeshBoundary(string fault)
    {
        var json = Triangle(); var node = json["nodes"]![0]!; var buffer = json["buffers"]![0]!;
        switch (fault)
        {
            case "matrix": node["matrix"] = JsonNode.Parse("[1,0,0,1,0,1,0,0,0,0,1,0,0,0,0,1]"); break;
            case "matrix-length": node["matrix"] = JsonNode.Parse("[1,0]"); break;
            case "quaternion": node["rotation"] = JsonNode.Parse("[0,0,0,2]"); break;
            case "translation-length": node["translation"] = JsonNode.Parse("[0,0]"); break;
            case "repeated-root": json["scenes"]![0]!["nodes"] = JsonNode.Parse("[0,0]"); break;
            case "cycle": node["children"] = JsonNode.Parse("[0]"); break;
            case "negative-node": json["scenes"]![0]!["nodes"] = JsonNode.Parse("[-1]"); break;
            case "far-node": json["scenes"]![0]!["nodes"] = JsonNode.Parse("[1]"); break;
            case "buffer-missing": buffer.AsObject().Remove("uri"); break;
            case "buffer-negative": buffer["byteLength"] = -1; break;
            case "buffer-size": buffer["byteLength"] = 1; break;
            case "empty-uri": buffer["uri"] = ""; break;
            case "uri-format": buffer["uri"] = "data:application/octet-stream,abcd"; break;
            case "uri-encoding": buffer["uri"] = "data:application/octet-stream;base64,!"; break;
            case "uri-network": buffer["uri"] = "https://example.invalid/model.bin"; break;
            case "uri-traversal": buffer["uri"] = "../outside.bin"; break;
            case "uri-control": buffer["uri"] = "bad\nresource.bin"; break;
        }
        string path = Path.Combine(_root.FullName, "witness.gltf"); File.WriteAllText(path, json.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => PackedModelDecoder.Decode(path));
        Assert.Equal(AddonStage.Mesh, AddonDiagnostics.Get(error)!.Stage);
    }

    [Fact]
    public void AnImplicitRootAndAffineMatrixPreserveTheAnalyticalTriangle()
    {
        var json = Triangle(); json.Remove("scenes"); json.Remove("scene");
        json["nodes"]![0]!["matrix"] = JsonNode.Parse("[1,0,0,0,0,1,0,0,0,0,1,0,2,3,4,1]");
        string path = Path.Combine(_root.FullName, "witness.gltf"); File.WriteAllText(path, json.ToJsonString());
        var model = PackedModelDecoder.Decode(path); var positions = Assert.Single(model.Surfaces).Positions.ToArray();
        Assert.Contains(new SpaceTangent.Contracts.Vector3d(2,3,4), positions);
        Assert.Contains(new SpaceTangent.Contracts.Vector3d(3,3,4), positions);
        Assert.Contains(new SpaceTangent.Contracts.Vector3d(2,4,4), positions);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
