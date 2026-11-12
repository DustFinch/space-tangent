// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LocalSkyPhotographTests
{
    [PhotographFact]
    [Trait("Category", "ExternalData")]
    public void ReadsAllOriginalFilesAndPreservesCoordinatesCreditsAndVisibility()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SKY_PHOTOGRAPHS")!;
        var images = LocalSkyPhotographs.Read(root);
        Assert.Equal(674, images.Count);
        Assert.Equal(images.Count, images.Select(image => image.Path).Distinct().Count());
        Assert.All(images, image =>
        {
            Assert.True(File.Exists(image.Path), image.Path);
            Assert.InRange(Math.Abs(image.Center.Length - 1), 0, 1e-12);
            Assert.InRange(image.RadiusRadians, 0, Math.PI / 2);
        });
        var crab = Assert.Single(images, image => Path.GetFileName(image.Path) == "m1dumont.png");
        Assert.Equal("Stephane Dumont", crab.Credit);
        Assert.Equal((0d, 1d), crab.Uvs[0]);
        Assert.Equal((1d, 0d), crab.Uvs[2]);
        var anchor = new Vector3d(.1018312808970496, .9219336262947943, .37372286381461456);
        Assert.InRange((crab.Corners[0] - anchor).Length, 0, 5e-12);
        Assert.Equal(2106215, crab.BirthJulianDate);
        Assert.Equal(0, crab.Opacity(.02, 7.6, 2106214.99));
        Assert.Equal(1, crab.Opacity(.02, 7.6, 2106215));
        Assert.Equal(0, crab.Opacity(.14, 7.6, 2106215));
        Assert.Equal(0, crab.Opacity(.02, -1, 2106215));
        Assert.Equal(.5, crab.Opacity(crab.MinimumResolutionDegrees / 1.5, 7.6, 2106215), 12);
        var seam = Assert.Single(images, image => Path.GetFileName(image.Path) == "abell85.png");
        Assert.InRange(Math.Abs(Math.Atan2(seam.Center.Y, seam.Center.X)), 0, .01);
        Assert.InRange(seam.RadiusRadians, .005, .02);
        var credited = Assert.Single(images, image => Path.GetFileName(image.Path) == "m2.png");
        Assert.Equal("Starhopper", credited.Credit);
        Assert.Equal("https://commons.wikimedia.org/wiki/File:M002.jpg", credited.CreditUrl);
    }

    private sealed class PhotographFactAttribute : FactAttribute
    {
        public PhotographFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SKY_PHOTOGRAPHS")))
                Skip = "Set the archived sky photograph catalogue.";
        }
    }
}
