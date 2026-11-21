// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SavedObserverContractTests
{
    private static readonly SimulationWorld World = new(new SharedClockTests.StaticEphemeris(), []);
    private const DeltaTModel Model = DeltaTModel.MorrisonStephenson2004;

    // Hand-authored legacy fields, not the current scenario serializer. A fixed-point
    // integer of 2^64 represents one micro light-year in the archived coordinate frame.
    private static JsonObject State() => JsonNode.Parse("""
        {"schemaVersion":1,"frame":"J2000EclipticYUp",
         "positionConvention":"universal-native-J2000-ecliptic-micro-light-year",
         "orientationConvention":"active-camera-to-frame-wxyz","selectedId":"Sol/Earth",
         "time":{"wholeDays":"2451545","nanosecondsOfDay":"123456789","scale":"Tdb"},
         "positionBits":{"x":"00000000000000010000000000000000","y":"00000000000000000000000000000000","z":"00000000000000000000000000000000"},
         "orientationBinary64":{"x":"0000000000000000","y":"0000000000000000","z":"0000000000000000","w":"3ff0000000000000"}}
        """)!.AsObject();

    private static SimulationScenario Read(JsonObject state)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(state.ToJsonString()));
        var result = SceneLink.ReadArchivedObserver(input, World, Model);
        Assert.True(input.CanRead);
        return result;
    }

    private static JsonObject Bookmark(int frame = 0) => new()
    {
        ["Name"] = "Earth – original pose", ["Observer"] = State().ToJsonString(),
        ["SchemaVersion"] = 3, ["Frame"] = frame, ["Rate"] = -600,
        ["FieldOfViewDegrees"] = 45, ["FrameReference"] = "Sol/Earth",
        ["FrameOther"] = "Sol"
    };

    private static SimulationScenario ReadBookmark(JsonObject bookmark) => SceneLink.Read(
        "celestia-godot:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(bookmark.ToJsonString())), World, Model).Scenario;

    [Fact]
    public void ImportsFixedPointPositionAndActiveQuaternionWithoutChangingTimeOrDeltaT()
    {
        var scene = Read(State());
        var view = Assert.Single(scene.Views);
        Assert.Equal(Model, scene.DeltaT);
        Assert.Equal(new JulianInstant(2451545, 123456789, TimeScale.Tdb), view.Epoch);
        Assert.Equal(SolarSystemSample.EarthId, view.Selection);
        var camera = view.Navigation!.Value;
        Assert.Null(camera.Target);
        Assert.False(camera.Tracking);
        Assert.InRange(camera.AnchorKilometers.X, 9460730.4725, 9460730.4727);
        Assert.Equal(0, camera.AnchorKilometers.Y);
        Assert.Equal(0, camera.AnchorKilometers.Z);
        var pose = camera.ResolvePlacement(World.Graph.Evaluate(view.Epoch, Model), Model);
        Assert.InRange(Math.Abs(pose.Forward.Length - 1), 0, 1e-14);
        Assert.InRange(Math.Abs(pose.Up.Length - 1), 0, 1e-14);
        Assert.InRange(Math.Abs(Vector3d.Dot(pose.Forward, pose.Up)), 0, 1e-14);
    }

    [Theory]
    [InlineData(0, NavigationFrame.Inertial, false)]
    [InlineData(1, NavigationFrame.Inertial, true)]
    [InlineData(3, NavigationFrame.PhaseLock, false)]
    [InlineData(4, NavigationFrame.Inertial, false)]
    [InlineData(5, NavigationFrame.Equatorial, false)]
    [InlineData(7, NavigationFrame.Inertial, false)]
    public void BookmarkFramesRetainTheirAbsolutePoseAndPlayback(int mode, NavigationFrame frame, bool bodyFixed)
    {
        var scene = ReadBookmark(Bookmark(mode));
        var view = Assert.Single(scene.Views);
        Assert.Equal(-600, view.PlaybackRate);
        Assert.Equal(45, view.FieldOfViewDegrees);
        Assert.Equal(Model, scene.DeltaT);
        var camera = view.Navigation!.Value;
        Assert.True(camera.IsValid());
        Assert.Equal(frame, camera.Placement!.Value.Frame);
        Assert.Equal(bodyFixed, camera.Placement.Value.BodyFixed);
        Assert.Equal(mode == 0 ? null : SolarSystemSample.EarthId, camera.Target);
        var snapshot = World.Graph.Evaluate(view.Epoch, Model);
        var pose = camera.ResolvePlacement(snapshot, Model);
        Assert.InRange(Math.Abs(Vector3d.Dot(pose.Up, pose.Forward)), 0, 1e-12);
        // Restoring a frame must retain the inertial eye rather than reinterpret its offset.
        Assert.InRange((camera.AnchorKilometers + pose.OffsetKilometers - new Vector3d(9460730.4725808, 0, 0)).Length, 0, .0001);
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("frame", "\"ICRS\"")]
    [InlineData("positionConvention", "\"kilometers\"")]
    [InlineData("orientationConvention", "\"passive\"")]
    [InlineData("selectedId", "\"\"")]
    [InlineData("selectedId", "\"missing-body\"")]
    public void RejectsIncompatibleCoordinateDeclarations(string key, string json)
    {
        var state = State(); state[key] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => Read(state));
    }

    [Theory]
    [InlineData("scale", "not-a-scale")]
    [InlineData("scale", "999")]
    [InlineData("wholeDays", "0")]
    [InlineData("wholeDays", "4000001")]
    public void RejectsUnavailableTimeBeforeRestoringCamera(string key, string value)
    {
        var state = State(); state["time"]![key] = value;
        Assert.Throws<InvalidDataException>(() => Read(state));
    }

    [Theory]
    [InlineData("positionBits", "x", "0")]
    [InlineData("orientationBinary64", "w", "0")]
    [InlineData("orientationBinary64", "w", "0000000000000000")]
    [InlineData("orientationBinary64", "w", "7ff0000000000000")]
    [InlineData("orientationBinary64", "x", "7ff8000000000000")]
    public void RejectsMalformedBitsAndNonUnitRotations(string section, string key, string value)
    {
        var state = State(); state[section]![key] = value;
        Assert.Throws<InvalidDataException>(() => Read(state));
    }

    [Theory]
    [InlineData("SchemaVersion", "4")]
    [InlineData("Frame", "8")]
    [InlineData("Rate", "1e20")]
    [InlineData("FieldOfViewDegrees", "0")]
    [InlineData("FieldOfViewDegrees", "180")]
    public void RejectsUnsupportedBookmarkControls(string key, string json)
    {
        var bookmark = Bookmark(); bookmark[key] = JsonNode.Parse(json);
        Assert.Throws<InvalidDataException>(() => ReadBookmark(bookmark));
    }

    [Fact]
    public void CatalogueFrameRequiresAnOwnerAndSelectionDate()
    {
        var bookmark = Bookmark(6);
        Assert.Throws<InvalidDataException>(() => ReadBookmark(bookmark));
        bookmark["CatalogFrameOwner"] = "Sol/Earth";
        Assert.Throws<InvalidDataException>(() => ReadBookmark(bookmark));
        bookmark["CatalogFrameSelectionJd"] = -1;
        Assert.Throws<InvalidDataException>(() => ReadBookmark(bookmark));
    }

    [Fact]
    public void DuplicateFieldsAndOversizedObserverAreRejectedAndLeaveCallerStreamOpen()
    {
        string json = State().ToJsonString();
        using var duplicate = new MemoryStream(Encoding.UTF8.GetBytes(json.Insert(1, "\"schemaVersion\":1,")));
        Assert.Throws<InvalidDataException>(() => SceneLink.ReadArchivedObserver(duplicate, World, Model));
        Assert.True(duplicate.CanRead);
        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 8193)));
        Assert.Throws<InvalidDataException>(() => SceneLink.ReadArchivedObserver(oversized, World, Model));
        Assert.True(oversized.CanRead);
    }

    [Fact]
    public void ImportSkipsBadAndDuplicateRowsAndPersistsOnlyUsableBookmarks()
    {
        var directory = Directory.CreateTempSubdirectory("st-bookmark-contract-");
        try
        {
            string source = Path.Combine(directory.FullName, "original.json");
            var valid = Bookmark();
            var duplicate = Bookmark(); duplicate["Name"] = "earth – ORIGINAL POSE";
            var invalid = Bookmark(); invalid["Observer"] = "{}";
            File.WriteAllText(source, new JsonArray(valid, duplicate, invalid).ToJsonString());
            var file = new ObserverBookmarkFile(Path.Combine(directory.FullName, "saved.json"));
            var result = file.ImportFavorites(source, [], World, Model);
            Assert.Equal((1, 2), (result.Added, result.Skipped));
            var entry = Assert.Single(file.Load());
            Assert.Equal("Earth – original pose", entry.Name);
            Assert.Equal(-600, SceneLink.Read(entry.Link, World, Model).Scenario.Views.Single().PlaybackRate);
            byte[] saved = File.ReadAllBytes(Path.Combine(directory.FullName, "saved.json"));
            var again = file.ImportFavorites(source, result.Bookmarks, World, Model);
            Assert.Equal((0, 3), (again.Added, again.Skipped));
            Assert.Equal(saved, File.ReadAllBytes(Path.Combine(directory.FullName, "saved.json")));
            Assert.Empty(Directory.GetFiles(directory.FullName, ".bookmarks-*"));
        }
        finally { directory.Delete(true); }
    }
}
