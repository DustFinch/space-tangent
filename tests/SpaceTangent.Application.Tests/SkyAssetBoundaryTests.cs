// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SkyAssetBoundaryTests
{
    private static JsonObject Tile() => JsonNode.Parse("""
        {"imageUrl":"field.png","minResolution":0.1,"maxBrightness":18,"birthJD":2450000,
         "worldCoords":[[[359,-1],[1,-1],[1,1],[359,1]]],
         "textureCoords":[[[0,0],[1,0],[1,1],[0,1]]],
         "imageCredits":{"SHORT":"Observer","infoUrl":"https://example.org/source","ignored":42}}
        """)!.AsObject();

    private static IReadOnlyList<LocalSkyPhotograph> Read(JsonNode node)
    {
        var directory = Directory.CreateTempSubdirectory("st-photo-contract-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "textures.json"), node.ToJsonString());
            return LocalSkyPhotographs.Read(directory.FullName);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SeamCrossingTileHasUnitCornersAndContinuousCenter()
    {
        var photo = Assert.Single(Read(Tile()));
        Assert.Equal("Observer", photo.Credit);
        Assert.Equal("https://example.org/source", photo.CreditUrl);
        Assert.InRange((photo.Center - new Vector3d(1, 0, 0)).Length, 0, 1e-14);
        Assert.All(photo.Corners, point => Assert.InRange(Math.Abs(point.Length - 1), 0, 1e-14));
        Assert.InRange(photo.RadiusRadians, .0246, .0248);
        Assert.Equal(new[] { (0d, 1d), (1d, 1d), (1d, 0d), (0d, 0d) }, photo.Uvs);
        Assert.Equal(0, photo.Opacity(.01, 7.6, 2449999));
        Assert.Equal(1, photo.Opacity(.01, 7.6, 2450000));
        Assert.Equal(0, photo.Opacity(.1, 7.6, 2450000));
        Assert.Equal(0, photo.Opacity(.01, -1, 2450000));
    }

    [Fact]
    public void ChildrenInheritResolutionAndUntrustedCreditUrlsAreNotPublished()
    {
        var child = Tile(); child.Remove("minResolution");
        child["imageCredits"]!["infoUrl"] = "file:///secret.png";
        var root = new JsonObject { ["minResolution"] = .25, ["subTiles"] = new JsonArray(child, 42) };
        var photo = Assert.Single(Read(root));
        Assert.Equal(.25, photo.MinimumResolutionDegrees);
        Assert.Null(photo.CreditUrl);
    }

    [Theory]
    [InlineData("imageUrl", "\"../escape.png\"")]
    [InlineData("imageUrl", "\"https://example.org/field.png\"")]
    [InlineData("imageUrl", "\"field.jpg\"")]
    [InlineData("imageUrl", "42")]
    [InlineData("minResolution", "0")]
    [InlineData("worldCoords", "[]")]
    [InlineData("worldCoords", "[[[0,91],[1,0],[1,1],[0,1]]]")]
    [InlineData("worldCoords", "[[[0,0],[0,0],[0,0],[0,0]]]")]
    [InlineData("worldCoords", "[[[0,0],[90,0],[180,0],[270,0]]]")]
    [InlineData("worldCoords", "[[[0,0],[1,0],[1,1],[\"0\",1]]]")]
    [InlineData("textureCoords", "[[[0,0],[2,0],[1,1],[0,1]]]")]
    [InlineData("textureCoords", "[[[0,0],[1,0],[1],[0,1]]]")]
    public void MalformedTileCannotPublishPartialImage(string key, string json)
    {
        var tile = Tile(); tile[key] = JsonNode.Parse(json);
        Assert.Empty(Read(tile));
    }

    [Fact]
    public void MetadataSizeAndImageCountAreBounded()
    {
        var directory = Directory.CreateTempSubdirectory("st-photo-limit-");
        try
        {
            string path = Path.Combine(directory.FullName, "textures.json");
            using (var stream = File.Create(path)) { stream.SetLength(4 * 1024 * 1024 + 1); }
            Assert.Throws<InvalidDataException>(() => LocalSkyPhotographs.Read(directory.FullName));
            string row = Tile().ToJsonString();
            File.WriteAllText(path, "{\"subTiles\":[" + string.Join(',', Enumerable.Repeat(row, 8193)) + "]}");
            Assert.Throws<InvalidDataException>(() => LocalSkyPhotographs.Read(directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    private const string Header = "class,designation,name,epoch,a,e,i,node,peri,M,H,K\n";

    [Theory]
    [InlineData("MB", 0)] [InlineData("NE", 1)] [InlineData("TJ", 2)]
    [InlineData("CE", 3)] [InlineData("TN", 4)] [InlineData("CO", 5)]
    public void CircularSmallBodiesFollowAnalyticQuarterOrbitAndPhotometry(string group, int index)
    {
        var entry = Assert.Single(MinorBodySwarm.Read(new StringReader(
            "# Original format comment\n\n" + Header + group + ",1,Test,2451545,1,0,0,0,0,0,10,4\n")));
        Assert.Equal(index, entry.Group);
        const double au = 149597870.7;
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        Assert.InRange((entry.Position(epoch) - new Vector3d(au, 0, 0)).Length, 0, 1e-7);
        var quarter = entry.Position(epoch.AddSeconds(Math.PI / (2 * .01720209895) * 86400));
        Assert.InRange(Math.Abs(quarter.X), 0, .001);
        Assert.InRange(Math.Abs(quarter.Length - au), 0, 1e-6);
        Assert.True(quarter.Y > 1e8 && quarter.Z > 5e7);
        Assert.Equal(10, entry.Magnitude(new(au, 0, 0), default), 10);
        Assert.Equal(double.PositiveInfinity, entry.Magnitude(default, new(au, 0, 0)));
        Assert.Equal(double.PositiveInfinity, entry.Magnitude(new(au, 0, 0), new(au, 0, 0)));
    }

    [Theory]
    [InlineData(0.8)] [InlineData(0.99)]
    public void EccentricOrbitHitsAnalyticPerihelionAndAphelion(double eccentricity)
    {
        var entry = Assert.Single(MinorBodySwarm.Read(new StringReader(Header +
            FormattableString.Invariant($"MB,1,Test,2451545,1,{eccentricity},0,0,0,0,10,4\n"))));
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        Assert.Equal(149597870.7 * (1 - eccentricity), entry.Position(epoch).Length, 5);
        Assert.Equal(149597870.7 * (1 + eccentricity),
            entry.Position(epoch.AddSeconds(Math.PI / .01720209895 * 86400)).Length, 4);
    }

    [Theory]
    [InlineData("")] [InlineData("wrong\n")] [InlineData("# only comment")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\n")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nunknown,1,Test,2451545,1,0,0,0,0,0,10,4")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nMB,,Test,2451545,1,0,0,0,0,0,10,4")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nMB,1,Test,2451545,1,1,0,0,0,0,10,4")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nMB,1,Test,2451545,NaN,0,0,0,0,0,10,4")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nMB,1,Test,2451545,0,0,0,0,0,0,10,4")]
    [InlineData("class,designation,name,epoch,a,e,i,node,peri,M,H,K\nMB,1,Test,0,1,0,0,0,0,0,10,4")]
    public void InvalidCatalogueCannotReturnAnOrbit(string csv) =>
        Assert.Throws<InvalidDataException>(() => MinorBodySwarm.Read(new StringReader(csv)));
}
