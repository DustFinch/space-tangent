// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class GuideAndFavoriteContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-guide-contract-");
    private string FileOf(string name, string contents)
    {
        string path = Path.Combine(_root.FullName, name); File.WriteAllText(path, contents); return path;
    }

    [Theory]
    [InlineData("km", 2d)]
    [InlineData("au", 299195741.4)]
    [InlineData("ly", 18921460945161.6)]
    public void GuideUnitsProduceTheExplicitKilometreFlightRange(string unit, double expected)
    {
        var result = TourConverter.ConvertFile(FileOf("guide.cel",
            "{ Name \"Moon\" Target \"Sol/Earth/Moon\" Distance 2 DistanceUnits \"" + unit + "\" }"));
        Assert.Equal(1, result.Commands);
        Assert.Empty(result.Unapplied);
        var match = Regex.Match(result.Lua, @"gotodistance\(st.selection\(\), ([^,]+), 1.5\)");
        Assert.True(match.Success);
        double actual = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(Math.Abs(actual - expected), 0, expected * 1e-12);
    }

    [Fact]
    public void ConvertedGuideExecutesItsValidStopsAndSkipsAnUnavailableTarget()
    {
        var conversion = TourConverter.ConvertFile(FileOf("guide.cel", """
            { Name "Missing" Target "Not Installed" }
            { Name "Earth" Target "Sol/Earth" Description "Arrival" }
            { Name "Moon" Target "Sol/Earth/Moon" }
            """));
        Assert.Equal(3, conversion.Commands);
        var session = SharedClockTests.Create(); session.OpenSpaceView(0);
        var other = session[0] with { Epoch = session[0].Epoch.AddSeconds(120), Playing = false, PlaybackRate = -10 };
        session.RestoreScenario(new([session[0], other], false, 0, 0, session.DeltaT));
        var log = new List<string>();
        var script = LuaSessionScript.Load(FileOf("converted.lua", conversion.Lua), session, 0, false, log.Add);
        for (int frame = 0; frame < 800 && (!script.Finished || session.IsFlying(0)); frame++)
        {
            script.Step(.125);
            session.UpdateCamera(0, session.World.Graph.Evaluate(session[0].Epoch), .125);
        }
        Assert.True(script.Finished); Assert.Null(script.Error); Assert.False(session.IsFlying(0));
        Assert.Equal(SolarSystemSample.MoonId, session[0].Selection);
        Assert.Equal(SolarSystemSample.MoonId, session[0].Navigation!.Value.Target);
        Assert.True(session[0].Navigation!.Value.IsValid());
        Assert.Equal(other, session[1]);
        Assert.Contains(log, line => line.Contains("Not Installed", StringComparison.Ordinal));
    }

    [Fact]
    public void GuideReportsUnappliedFieldsAndTruncationWithoutLosingTheStopName()
    {
        var result = TourConverter.ConvertFile(FileOf("guide.cel", "{ Name \"Arrival\" Target \"Earth\" " +
            "Description \"" + new string('x', 600) + "\" Unknown \"value\" }"));
        Assert.Equal(1, result.Commands);
        Assert.Contains("guide field Unknown", result.Unapplied);
        Assert.Contains(result.Unapplied, note => note.Contains("exceeds 512", StringComparison.Ordinal));
        Assert.Contains("st.notice(\"Arrival\", 3)", result.Lua);
        Assert.DoesNotContain(new string('x', 600), result.Lua);
    }

    [Theory]
    [InlineData("{ Name \"No target\" }")]
    [InlineData("{ Target \"Earth\" Target \"Moon\" }")]
    [InlineData("{ Target \"Earth\" broken-key x }")]
    [InlineData("{ Target \"Earth\" Description { }")]
    [InlineData("{ Target \"Earth\" Distance 0 }")]
    [InlineData("{ Target \"Earth\" Distance -1 }")]
    [InlineData("{ Target \"Earth\" Distance 1 DistanceUnits \"meter\" }")]
    [InlineData("{ Target \"Earth\" Distance 1e300 DistanceUnits \"ly\" }")]
    [InlineData("{ Target \"Earth\"")]
    public void InvalidGuideCannotReturnExecutablePartialTour(string input) =>
        Assert.Throws<InvalidDataException>(() => TourConverter.ConvertFile(FileOf("bad.cel", input)));

    [Fact]
    public void GuideStopAndCaptionLimitsAreExplicit()
    {
        Assert.Throws<InvalidDataException>(() => TourConverter.ConvertFile(FileOf("many.cel",
            string.Concat(Enumerable.Repeat("{ Target \"Earth\" }\n", 33)))));
        Assert.Throws<InvalidDataException>(() => TourConverter.ConvertFile(FileOf("long-name.cel",
            "{ Target \"Earth\" Name \"" + new string('n', 513) + "\" }")));
        Assert.Throws<NotSupportedException>(() => TourConverter.ConvertFile(FileOf("guide.txt", "{}")));
        Assert.Throws<InvalidDataException>(() => TourConverter.ConvertFile(FileOf("large.cel", new string(' ', 32769))));
    }

    [Theory]
    [InlineData("universal", false, false)]
    [InlineData("ecliptical", true, false)]
    [InlineData("geographic", true, true)]
    public void FavoritesImportRetainsUniversalEyeAcrossReferenceFrames(string coordinates, bool tracked, bool fixedBody)
    {
        string source = FileOf("favorites.cel", "\"View\" { parentFolder \"Earth\" base [0.000001 0 0] " +
            "axis [0 0 1] angle 1.5707963267948966 time 2451545 selection \"Sol/Earth\" coordsys \"" + coordinates + "\" }");
        var world = new SimulationWorld(new SharedClockTests.StaticEphemeris(), []);
        var bookmarks = new ObserverBookmarkFile(Path.Combine(_root.FullName, "bookmarks.json"));
        var result = bookmarks.ImportFavorites(source, [], world, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal((1, 0), (result.Added, result.Skipped));
        var saved = Assert.Single(bookmarks.Load()); Assert.Equal("Earth / View", saved.Name);
        var scene = SceneLink.Read(saved.Link, world, DeltaTModel.EspenakMeeus2006).Scenario;
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, scene.DeltaT);
        var observer = Assert.Single(scene.Views);
        Assert.Equal(SolarSystemSample.EarthId, observer.Selection);
        var camera = observer.Navigation!.Value;
        Assert.Equal(tracked, camera.Tracking);
        Assert.Equal(fixedBody, camera.Placement!.Value.BodyFixed);
        Assert.Equal(tracked ? SolarSystemSample.EarthId : null, camera.Target);
        var snapshot = world.Graph.Evaluate(observer.Epoch, scene.DeltaT);
        var pose = camera.ResolvePlacement(snapshot, scene.DeltaT);
        var expected = snapshot.SolarSystem!.Value.Sun.PositionKilometers + new Vector3d(9460730.4725808, 0, 0);
        Assert.InRange((camera.AnchorKilometers + pose.OffsetKilometers - expected).Length, 0, .0001);
        Assert.InRange(Math.Abs(Vector3d.Dot(pose.Up, pose.Forward)), 0, 1e-12);
    }

    [Fact]
    public void OutOfCoverageFavoriteIsSkippedAndUnknownSelectionFallsBackToSun()
    {
        string source = FileOf("favorites.cel", """
            "Uncovered" { time 0 selection "Earth" }
            "Unknown target" { time 2451545 selection "not installed" base [0.000001 0 0] }
            "unknown TARGET" { time 2451545 selection "Sun" }
            """
        );
        var world = new SimulationWorld(new SharedClockTests.StaticEphemeris(), []);
        var file = new ObserverBookmarkFile(Path.Combine(_root.FullName, "saved.json"));
        var result = file.ImportFavorites(source, [], world, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal((1, 2), (result.Added, result.Skipped));
        var restored = SceneLink.Read(Assert.Single(result.Bookmarks).Link, world, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal(SolarSystemSample.SunId, Assert.Single(restored.Scenario.Views).Selection);
    }

    public void Dispose() => _root.Delete(true);
}
