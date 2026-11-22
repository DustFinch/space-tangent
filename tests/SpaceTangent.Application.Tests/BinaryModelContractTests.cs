// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class BinaryModelContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-binary-model-");
    private string Write(Action<BinaryWriter> body)
    {
        string file = Path.Combine(_root.FullName, "model.cmod");
        using var output = new BinaryWriter(File.Create(file));
        output.Write("#celmodel_binary"u8);
        body(output);
        return file;
    }

    private static void Token(BinaryWriter output, params short[] tokens)
    { foreach (short value in tokens) output.Write(value); }

    private static void Vertices(BinaryWriter output, bool packed = false)
    {
        Token(output, 1009, 1011, 0, 2, 1, packed ? (short)4 : (short)3, 9, 0, 5, 1, 1012, 1013);
        output.Write(4u);
        float[][] points = [[0, 0, 0], [2, 0, 0], [0, 2, 0], [2, 2, 0]];
        for (int index = 0; index < points.Length; index++)
        {
            foreach (float component in points[index]) output.Write(component);
            if (packed) output.Write(0x80402010u);
            else { output.Write(.25f); output.Write(.5f); output.Write(.75f); output.Write(.5f); }
            output.Write(index == 0 ? -2f : 2f);
            output.Write(index % 2 == 0 ? 0f : 1f);
            output.Write(index < 2 ? 0f : 1f);
        }
    }

    private static void Group(BinaryWriter output, short kind, uint material, params uint[] indices)
    {
        output.Write(kind); output.Write(material); output.Write((uint)indices.Length);
        foreach (uint index in indices) output.Write(index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointSpritesPreserveColorOpacitySizeAndNormalizeOnlySpriteReads(bool packed)
    {
        string path = Write(output =>
        {
            Token(output, 1001, 1006, 1); output.Write(.5f); Token(output, 1002);
            Vertices(output, packed); Group(output, 6, 0, 0, 1, 2, 3); Token(output, 1010);
        });
        var original = CmodMeshDecoder.DecodeForModelExport(_root.FullName, path);
        Assert.Empty(original.Surfaces);
        Assert.Equal(4, original.Sprites.Count);
        Assert.Equal(new Vector3d(2, 2, 0), original.Sprites[3].Position);
        Assert.Equal(0, original.Sprites[0].Size);
        Assert.Equal(2, original.Sprites[1].Size);
        Assert.Equal(packed ? new Vector3d(16 / 255d, 32 / 255d, 64 / 255d) : new(.25, .5, .75), original.Sprites[0].Color);
        Assert.Equal(packed ? 64 / 255d : .25, original.Sprites[0].Opacity, 12);
        var normalized = CmodMeshDecoder.DecodeSprites(path);
        Assert.Equal(new Vector3d(-1, -1, 0), normalized[0].Position);
        Assert.Equal(new Vector3d(1, 1, 0), normalized[3].Position);
        Assert.Equal(original.Sprites[2].Color, normalized[2].Color);
        Assert.Throws<NotSupportedException>(() => CmodMeshDecoder.DecodeForTriangleExport(_root.FullName, path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TriangleTopologiesRetainWindingAndBodyTextureConvention(short topology)
    {
        uint[] indices = topology == 0 ? [0, 1, 2, 1, 3, 2] : topology == 1 ? [0, 1, 2, 3] : [0, 1, 3, 2];
        string path = Write(output => { Vertices(output); Group(output, topology, uint.MaxValue, indices); Token(output, 1010); });
        var normal = Assert.Single(CmodMeshDecoder.Decode(_root.FullName, path).Surfaces);
        var native = Assert.Single(CmodMeshDecoder.DecodeNativeBody(_root.FullName, path).Surfaces);
        Assert.Equal(6, normal.Positions.Length);
        for (int offset = 0; offset < 6; offset += 3)
        {
            var p = normal.Positions.Span;
            Assert.True(Vector3d.Cross(p[offset + 1] - p[offset], p[offset + 2] - p[offset]).Z > 0);
            Assert.Equal(p[offset], native.Positions.Span[offset]);
            Assert.Equal(p[offset + 1], native.Positions.Span[offset + 2]);
            Assert.Equal(p[offset + 2], native.Positions.Span[offset + 1]);
        }
        Assert.Equal(1, normal.TextureCoordinates.Span[1]);
        Assert.Equal(0, native.TextureCoordinates.Span[1]);
        Assert.Throws<InvalidDataException>(() => CmodMeshDecoder.DecodeSprites(path));
    }

    [Fact]
    public void SpriteExtractionIncludesTriangleBoundsAndModelExportRejectsLines()
    {
        string path = Write(output =>
        {
            Vertices(output); Group(output, 0, uint.MaxValue, 0, 1, 2);
            Group(output, 5, uint.MaxValue, 3); Token(output, 1010);
        });
        Assert.Single(CmodMeshDecoder.DecodeForModelExport(_root.FullName, path).Surfaces);
        Assert.Equal(new Vector3d(1, 1, 0), Assert.Single(CmodMeshDecoder.DecodeSprites(path)).Position);
        path = Write(output => { Vertices(output); Group(output, 3, uint.MaxValue, 0, 1); Token(output, 1010); });
        Assert.Throws<NotSupportedException>(() => CmodMeshDecoder.DecodeForModelExport(_root.FullName, path));
        Assert.Throws<InvalidDataException>(() => CmodMeshDecoder.Decode(_root.FullName, path));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("material")]
    [InlineData("typed-color")]
    [InlineData("nan")]
    [InlineData("texture-length")]
    [InlineData("duplicate-attribute")]
    [InlineData("attribute-range")]
    [InlineData("missing-position")]
    [InlineData("zero-vertices")]
    [InlineData("large-vertices")]
    [InlineData("group-range")]
    [InlineData("large-group")]
    [InlineData("incomplete-triangle")]
    [InlineData("missing-material")]
    [InlineData("vertex-index")]
    [InlineData("materials-limit")]
    public void InvalidBinaryModelsFailAtTheMeshBoundary(string fault)
    {
        string path = Write(output =>
        {
            switch (fault)
            {
                case "block": Token(output, 999); return;
                case "material": Token(output, 1001, 999); return;
                case "typed-color": Token(output, 1001, 1003, 1); return;
                case "nan": Token(output, 1001, 1003, 7); output.Write(float.NaN); return;
                case "texture-length": Token(output, 1001, 1007, 0, 5, 1025); return;
                case "duplicate-attribute": Token(output, 1009, 1011, 0, 2, 0, 2); return;
                case "attribute-range": Token(output, 1009, 1011, 12, 2); return;
                case "missing-position": Token(output, 1009, 1011, 1, 2, 1012); return;
                case "zero-vertices": Token(output, 1009, 1011, 0, 2, 1012, 1013); output.Write(0u); return;
                case "large-vertices": Token(output, 1009, 1011, 0, 2, 1012, 1013); output.Write(1_000_001u); return;
                case "materials-limit": for (int i = 0; i < 4097; i++) Token(output, 1001, 1002); return;
            }
            Vertices(output);
            switch (fault)
            {
                case "group-range": Group(output, 7, uint.MaxValue, 0); break;
                case "large-group": Token(output, 0); output.Write(uint.MaxValue); output.Write(3_000_001u); break;
                case "incomplete-triangle": Group(output, 0, uint.MaxValue, 0, 1); break;
                case "missing-material": Group(output, 0, 0, 0, 1, 2); break;
                case "vertex-index": Group(output, 0, uint.MaxValue, 0, 1, 4); break;
            }
            Token(output, 1010);
        });
        var error = Assert.Throws<InvalidDataException>(() => CmodMeshDecoder.Decode(_root.FullName, path));
        Assert.Equal(AddonStage.Mesh, AddonDiagnostics.Get(error)!.Stage);
    }

    [Fact]
    public void UnknownHeaderAndCancellationDoNotReturnPartialGeometry()
    {
        string path = Write(output => { Vertices(output); Group(output, 0, uint.MaxValue, 0, 1, 2); Token(output, 1010); });
        Assert.Throws<OperationCanceledException>(() => CmodMeshDecoder.DecodeNativeBody(_root.FullName, path, new(true)));
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("invalid-header!!"));
        Assert.Throws<NotSupportedException>(() => CmodMeshDecoder.Decode(_root.FullName, path));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
