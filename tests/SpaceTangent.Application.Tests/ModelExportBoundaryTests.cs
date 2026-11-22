// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ModelExportBoundaryTests
{
    private static DecodedSurface Triangle => new(new Vector3d[]{new(0,0,0),new(1,0,0),new(0,1,0)},
        new Vector3d[]{new(0,0,1),new(0,0,1),new(0,0,1)},new float[6],new(new(1,1,1),1,null));

    [Theory]
    [InlineData("empty")] [InlineData("many-surfaces")] [InlineData("short-normals")]
    [InlineData("short-uv")] [InlineData("coordinates")] [InlineData("missing-texture")]
    public void InvalidModelsCannotWriteAPartialGlb(string fault)
    {
        var surface=Triangle;
        surface=fault switch
        {
            "short-normals"=>surface with {Normals=ReadOnlyMemory<Vector3d>.Empty},
            "short-uv"=>surface with {TextureCoordinates=ReadOnlyMemory<float>.Empty},
            "coordinates"=>surface with {Positions=new Vector3d[]{new(double.NaN,0,0),new(1,0,0),new(0,1,0)}},
            "missing-texture"=>surface with {Material=surface.Material with {Texture="missing.png"}},
            _=>surface
        };
        IReadOnlyList<DecodedSurface> surfaces=fault=="empty"?[]:fault=="many-surfaces"?Enumerable.Repeat(surface,129).ToArray():[surface];
        using var output=new MemoryStream();
        Assert.Throws<InvalidDataException>(()=>PackedModelWriter.Write(new(surfaces,new Dictionary<string,ReadOnlyMemory<byte>>(),1),output));
        Assert.Equal(0,output.Length);
    }

    [Fact]
    public void CutoutMaterialsRetainTheirThresholdAndNormalizeHighDynamicRangeEmission()
    {
        var surface=Triangle; surface=surface with {Material=surface.Material with {AlphaCutoff=.375,AlphaBlend=true,
            Emission=new(3,1.5,.75),Specular=new(.1,.2,.3),SpecularPower=12}};
        using var output=new MemoryStream();
        PackedModelWriter.Write(new([surface],new Dictionary<string,ReadOnlyMemory<byte>>(),1),output);
        byte[] bytes=output.ToArray(); int size=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        using var json=JsonDocument.Parse(bytes.AsMemory(20,size)); var material=json.RootElement.GetProperty("materials")[0];
        Assert.Equal("MASK",material.GetProperty("alphaMode").GetString());
        Assert.Equal(.375,material.GetProperty("alphaCutoff").GetDouble());
        Assert.Equal(3,material.GetProperty("extensions").GetProperty("KHR_materials_emissive_strength").GetProperty("emissiveStrength").GetDouble());
        Assert.Equal(new[]{1,.5,.25},material.GetProperty("emissiveFactor").EnumerateArray().Select(item=>item.GetDouble()).ToArray());
        Assert.Equal(2,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(bytes.Length,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
    }
}
