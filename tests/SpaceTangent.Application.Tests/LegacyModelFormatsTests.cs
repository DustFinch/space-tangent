// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using System.Security.Cryptography;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyModelFormatsTests
{
    [ModelFormatFact]
    [Trait("Category", "ExternalData")]
    public void ReadsTheArchivedAsciiMaterialAndOriginalDisplacedSphere()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_MODEL_FORMATS")!;
        string models = Path.Combine(root, "models");
        var ascii = CmodMeshDecoder.Decode(root, Path.Combine(models, "ship.cmod"));
        DecodedSurface triangle = Assert.Single(ascii.Surfaces);
        Assert.Equal(new Vector3d[] { default, new(1, 0, 0), new(0, 1, 0) }, triangle.Positions.ToArray());
        Assert.Equal("panel.png", triangle.Material.Texture);
        Assert.Equal(File.ReadAllBytes(Path.Combine(models, "panel.png")), ascii.Textures["panel.png"].ToArray());

        var sphere = DisplacedSphereDecoder.Decode(Path.Combine(models, "roughsphere.cms"));
        var surface = Assert.Single(sphere.Surfaces);
        Assert.Equal(9800 * 3, surface.Positions.Length);
        var points = surface.Positions.ToArray();
        double x = points.Max(position => position.X) - points.Min(position => position.X);
        double y = points.Max(position => position.Y) - points.Min(position => position.Y);
        double z = points.Max(position => position.Z) - points.Min(position => position.Z);
        Assert.InRange(Math.Abs(x / z - 169.5284977555275 / 172.5), 0, 1e-6);
        Assert.InRange(Math.Abs(y / z - 169.75343272089958 / 172.5), 0, 1e-6);
    }

    [ModelFormatFact(owners: true)]
    [Trait("Category", "ExternalData")]
    public void ArchivedModelOwnersKeepDistinctTexturesWhenTheirPackIsRelocated()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_MODEL_OWNERS")!;
        var scratch = Directory.CreateTempSubdirectory("st-archive-model-");
        try
        {
            string destination = scratch.CreateSubdirectory("перенесённые модели").FullName;
            string packedRoot = Path.Combine(archive, "packed");
            foreach (string file in Directory.EnumerateFiles(packedRoot, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(destination, Path.GetRelativePath(packedRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            var source = Load(Path.Combine(archive, "source"));
            var packed = Load(destination);
            Assert.Equal(new[] { "jason", "sorce", "synthetic-0", "synthetic-1" }, packed.Select(body => body.Name).Order().ToArray());
            Assert.Equal(packed.Select(body => body.Name), source.Select(body => body.Name));
            Assert.Equal(4, packed.Select(body => body.MeshPath).Distinct().Count());

            var signatures = new Dictionary<string, string[]>();
            foreach (var body in source)
            {
                var relocated = Assert.Single(packed, item => item.Name == body.Name);
                var loose = ScienceModelDecoder.Decode(body.MeshPath!);
                var compiled = PackedModelDecoder.Decode(destination, relocated.MeshPath!);
                Assert.Equal(loose.Surfaces.Sum(surface => surface.Positions.Length),
                    compiled.Surfaces.Sum(surface => surface.Positions.Length));
                string[] textures = Hashes(loose);
                Assert.NotEmpty(textures);
                Assert.Equal(textures, Hashes(compiled));
                signatures.Add(body.Name, textures);
            }
            Assert.NotEqual(signatures["synthetic-0"], signatures["synthetic-1"]);
        }
        finally { scratch.Delete(recursive: true); }

        static IReadOnlyList<SampledMission> Load(string root) => SampledMissionCatalog.Load("pack.json", root,
            name => name is "Earth" or "Sol/Earth" ? new ObjectId("sol/earth") : null,
            warning => throw new InvalidDataException(warning));
        static string[] Hashes(DecodedScienceModel model) => model.Textures.Values
            .Select(bytes => Convert.ToHexString(SHA256.HashData(bytes.Span))).Order(StringComparer.Ordinal).ToArray();
    }


    private sealed class ModelFormatFactAttribute : FactAttribute
    {
        public ModelFormatFactAttribute(bool owners = false)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(owners ? "SPACETANGENT_ARCHIVED_MODEL_OWNERS" : "SPACETANGENT_LEGACY_MODEL_FORMATS")))
                Skip = "Set the archived model formats fixture.";
        }
    }
}
