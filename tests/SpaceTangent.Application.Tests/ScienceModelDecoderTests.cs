// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ScienceModelDecoderTests
{
    [RealModelsFact]
    public void DecodesTheOriginalModelsAndTheirTexturesWithoutAnEngine()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        Check(Path.Combine(root, "data", "models", "jason.obj"), 83_233, 28, 2);
        Check(Path.Combine(root, "catalog", "sorce.obj"), 115_711, 20, 1);
    }

    [Fact]
    public void CancellationAndInvalidFacesHaveExplicitFailures()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => ScienceModelDecoder.Decode(path, cancellation.Token));
            DecodedScienceModel triangle = ScienceModelDecoder.Decode(path);
            Assert.Equal(3, Assert.Single(triangle.Surfaces).Positions.Length);

            File.WriteAllText(path, "v 0 0 0\nf 1 2 3\n");
            Assert.Throws<InvalidDataException>(() => ScienceModelDecoder.Decode(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Check(string path, int triangles, int surfaces, int textures)
    {
        DecodedScienceModel model = ScienceModelDecoder.Decode(path);
        Assert.Equal(surfaces, model.Surfaces.Count);
        Assert.Equal(triangles * 3, model.Surfaces.Sum(surface => surface.Positions.Length));
        Assert.Equal(textures, model.Textures.Count);
        Assert.True(model.Diameter > 0);
        Assert.All(model.Surfaces, surface => Assert.Equal(1, surface.Material.Opacity));
    }

    private sealed class RealModelsFactAttribute : FactAttribute
    {
        public RealModelsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set SPACETANGENT_SCIENCE_ROOT to run the original-model regressions.";
        }
    }
}
