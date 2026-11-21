// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ModelFormatContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-model-contract-");
    private const string Triangle = """
        material
        diffuse 0.25 0.5 0.75
        emissive 0.125 0 0.25
        specular 1 0.5 0
        opacity 0.75
        specpower 16
        end_material
        mesh
        vertexdesc position f3 normal f3 texcoord0 f2 end_vertexdesc
        vertices 3
        0 0 0 0 0 1 0 0
        1 0 0 0 0 1 1 0
        0 1 0 0 0 1 0 1
        trilist 0 3 0 1 2
        end_mesh
        """;
    private string FileOf(string name, string content)
    {
        string path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }
    private DecodedScienceModel Cmod(string content, CancellationToken cancellation = default) =>
        CmodMeshDecoder.Decode(_root.FullName, FileOf("input.cmod", "#celmodel__ascii\n" + content), cancellation);
    private DecodedScienceModel Sphere(string fields, CancellationToken cancellation = default) =>
        DisplacedSphereDecoder.Decode(FileOf("input.cms", "SphereDisplacementMesh { " + fields + " }"), cancellation);

    [Fact]
    public void AsciiTriangleRetainsMaterialChannelsAndGeometry()
    {
        var decoded = Cmod(Triangle);
        var surface = Assert.Single(decoded.Surfaces);
        Assert.Equal(3, surface.Positions.Length);
        Assert.Equal(3, surface.Normals.Length);
        Assert.Equal(6, surface.TextureCoordinates.Length);
        Assert.Equal(new Vector3d(.25, .5, .75), surface.Material.Color);
        // Emission is linear light; the source material stores encoded sRGB components.
        Assert.InRange(Math.Abs(surface.Material.Emission.X - Math.Pow((.125 + .055) / 1.055, 2.4)), 0, 1e-12);
        Assert.InRange(Math.Abs(surface.Material.Emission.Z - Math.Pow((.25 + .055) / 1.055, 2.4)), 0, 1e-12);
        Assert.Equal(0, surface.Material.Emission.Y);
        Assert.Equal(new Vector3d(1, .5, 0), surface.Material.Specular);
        Assert.Equal(16, surface.Material.SpecularPower);
        Assert.Equal(.75, surface.Material.Opacity);
    }

    [Fact]
    public void CommentsAndWhitespaceDoNotChangeAsciiGeometry()
    {
        var plain = Cmod(Triangle);
        var commented = Cmod("# preamble\r\n" + Triangle.Replace("vertices 3", "vertices # count\n 3"));
        Assert.Equal(plain.Surfaces[0].Positions.ToArray(), commented.Surfaces[0].Positions.ToArray());
        Assert.Equal(plain.Surfaces[0].Material, commented.Surfaces[0].Material);
    }

    [Fact]
    public void AsciiModelSurvivesGlbExportWithoutLosingMaterialChannels()
    {
        var original = Cmod(Triangle);
        string path = Path.Combine(_root.FullName, "triangle.glb");
        using (var stream = File.Create(path)) PackedModelWriter.Write(original, stream);
        var restored = PackedModelDecoder.Decode(path);
        Assert.Equal(original.Surfaces[0].Positions.Length, restored.Surfaces[0].Positions.Length);
        Assert.Equal(original.Surfaces[0].Material.Emission, restored.Surfaces[0].Material.Emission);
        Assert.Equal(original.Surfaces[0].Material.Specular, restored.Surfaces[0].Material.Specular);
        Assert.Equal(original.Surfaces[0].Material.SpecularPower, restored.Surfaces[0].Material.SpecularPower);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("material roughness 1 end_material")]
    [InlineData("material diffuse NaN 0 0 end_material")]
    [InlineData("material texture0 \"unterminated")]
    [InlineData("mesh vertexdesc position bad end_vertexdesc")]
    [InlineData("mesh vertexdesc unknown f3 end_vertexdesc")]
    [InlineData("mesh vertexdesc position f3 end_vertexdesc vertices -1")]
    [InlineData("mesh vertexdesc position f3 end_vertexdesc vertices 1000001")]
    public void MalformedAsciiIsRejectedWithAMeshDiagnostic(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => Cmod(text));
        Assert.Equal(SpaceTangent.Core.Addons.AddonStage.Mesh, SpaceTangent.Core.Addons.AddonDiagnostics.Get(error)!.Stage);
    }

    [Fact]
    public void AsciiIndicesCannotReadBeyondTheDeclaredVertices()
    {
        Assert.Throws<InvalidDataException>(() => Cmod(Triangle.Replace("0 1 2", "0 1 3")));
    }

    [Fact]
    public void AsciiCancellationPreventsPublishingAModel()
    {
        Assert.Throws<OperationCanceledException>(() => Cmod(Triangle, new CancellationToken(true)));
    }

    [Fact]
    public void ExcessivelyLongAsciiTokensAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Cmod(new string('x', 1025)));
    }

    [Fact]
    public void AZeroNoiseSphereLiesOnItsAuthoredEllipsoid()
    {
        var model = Sphere("Size [2 3 4] Slices 8 Rings 5 FeatureHeight 0");
        var surface = Assert.Single(model.Surfaces);
        Assert.Equal(192, surface.Positions.Length);
        foreach (var point in surface.Positions.Span)
            Assert.InRange(Math.Abs(point.X * point.X / 4 + point.Y * point.Y / 9 + point.Z * point.Z / 16 - 1), 0, 1e-6);
        foreach (var normal in surface.Normals.Span)
            Assert.InRange(normal.Length, .999999, 1.000001);
        Assert.InRange(model.Diameter, Math.Sqrt(116) - 1e-6, Math.Sqrt(116) + 1e-6);
        Assert.All(surface.TextureCoordinates.ToArray(), uv => Assert.InRange(uv, 0, 1));
    }

    [Fact]
    public void DisplacedSphereNoiseIsDeterministicAcrossIndependentDecodes()
    {
        const string definition = "Size [2 3 4] Slices 8 Rings 5 FeatureHeight 0.1 Octaves 4 NoiseOffset [1 2 3]";
        var first = Sphere(definition);
        var second = Sphere(definition);
        Assert.Equal(first.Surfaces[0].Positions.ToArray(), second.Surfaces[0].Positions.ToArray());
        Assert.NotEqual(Sphere("Size [2 3 4] Slices 8 Rings 5").Surfaces[0].Positions.ToArray(), first.Surfaces[0].Positions.ToArray());
    }

    [Theory]
    [InlineData("Size [0 1 1]")]
    [InlineData("Size [-1 1 1]")]
    [InlineData("Slices 2")]
    [InlineData("Rings 2")]
    [InlineData("Octaves -1")]
    [InlineData("Slices 2000 Rings 1000")]
    [InlineData("Unknown 1")]
    [InlineData("Size [1 1 1] Size [2 2 2]")]
    public void InvalidSphereDimensionsAndGrammarAreRejected(string fields)
    {
        Assert.Throws<InvalidDataException>(() => Sphere(fields));
    }

    [Fact]
    public void SphereCancellationPreventsPublishingPartialTriangles()
    {
        Assert.Throws<OperationCanceledException>(() => Sphere("Slices 8 Rings 5", new CancellationToken(true)));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
