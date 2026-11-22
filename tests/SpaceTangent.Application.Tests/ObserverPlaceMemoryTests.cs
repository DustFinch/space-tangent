// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverPlaceMemoryTests
{
    private static readonly EarthObserverCamera Earth = EarthObserverCamera.Default with { Site = new(-33.9, 18.4, .02) };
    private static readonly EarthObserverCamera Moon = new(new(12, 34, .1), 25, 30) { Body = SolarSystemSample.MoonId };

    [Fact]
    public void PlacesAreImmutableAndSplitViewsForkIdentityWithoutChangingPhysicalState()
    {
        var empty = default(ObserverPlaceMemory);
        var first = empty.Remember(Earth);
        var second = first.Remember(Moon);
        Assert.Empty(empty.Locations);
        Assert.Single(first.Locations);
        Assert.Equal(Earth, second.Find(SolarSystemSample.EarthId));
        Assert.Null(first.Find(SolarSystemSample.MoonId));
        var fork = second.ForkIdentity();
        Assert.NotEqual(second.ViewKey, fork.ViewKey);
        Assert.Equal(second, fork);
        Assert.Equal(second.GetHashCode(), fork.GetHashCode());
        Assert.True(second.Equals((object)fork));
        Assert.False(second.Equals(null));
        Assert.True(second == fork);
        Assert.True(second != first);
        Assert.False(second == default);
        Assert.Equal(second.ViewKey, second.EnsureIdentity().ViewKey);
        Assert.Equal(second, second.Remember(Moon));
        Assert.True(second.IsValid());
    }

    [Fact]
    public void AFullMemoryEvictsTheOldestNonEarthBodyAndReplacesExistingBodies()
    {
        var memory = default(ObserverPlaceMemory).Remember(Earth);
        for (int body = 0; body < 20; body++) memory = memory.Remember(Moon with { Body = new("test/" + body) });
        Assert.Equal(16, memory.Locations.Count);
        Assert.Equal(Earth, memory.Find(SolarSystemSample.EarthId));
        Assert.Null(memory.Find(new("test/0")));
        memory = memory.Remember(Earth with { AzimuthDegrees = 42 });
        Assert.Equal(16, memory.Locations.Count);
        Assert.Equal(42, memory.Find(SolarSystemSample.EarthId)!.Value.AzimuthDegrees);
    }

    [Fact]
    public void SavedMemoryCopiesItsInputAndRejectsInvalidOrDuplicatePlaces()
    {
        EarthObserverCamera[] source = [Earth, Moon];
        var memory = ObserverPlaceMemory.FromSaved(Guid.NewGuid(), source);
        source[0] = Moon;
        Assert.Equal(Earth, memory.Locations[0]);
        Assert.Throws<InvalidDataException>(() => ObserverPlaceMemory.FromSaved(Guid.Empty, []));
        Assert.Throws<InvalidDataException>(() => ObserverPlaceMemory.FromSaved(Guid.NewGuid(), null!));
        Assert.Throws<InvalidDataException>(() => ObserverPlaceMemory.FromSaved(Guid.NewGuid(), [Earth, Earth]));
        Assert.Throws<InvalidDataException>(() => ObserverPlaceMemory.FromSaved(Guid.NewGuid(), [Moon with { ElevationDegrees = 91 }]));
        Assert.Throws<InvalidDataException>(() => ObserverPlaceMemory.FromSaved(Guid.NewGuid(), Enumerable.Repeat(Earth, 17).ToArray()));
        Assert.Throws<ArgumentException>(() => memory.Remember(Earth with { AzimuthDegrees = double.NaN }));
    }

    [ModelFact]
    public void BodySwitchingRestoresTheLastEarthPlaceAndKeepsViewsIndependentAfterClose()
    {
        var session = new SimulationSession(new(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), [],
            planets: new PlanetEphemerides(Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES")!)),
            new(2451545, 0, TimeScale.Tdb), SpaceTangent.Core.Time.DeltaTModel.FixedOffset);
        session.SetEarthObserver(0, Earth);
        Assert.True(session.SetObserverBody(0, SolarSystemSample.MoonId));
        session.SetEarthObserver(0, Moon);
        session.Split();
        Guid secondKey = session[1].Places.ViewKey;
        Assert.NotEqual(session[0].Places.ViewKey, secondKey);
        Assert.True(session.SetObserverBody(0, SolarSystemSample.EarthId));
        Assert.Equal(Earth, session[0].ObserverCamera);
        Assert.Equal(Moon, session[1].ObserverCamera);
        Assert.True(session.SetObserverBody(0, SolarSystemSample.MoonId));
        Assert.Equal(Moon, session[0].ObserverCamera);
        var before = session[0];
        Assert.False(session.SetObserverBody(0, new("missing/body")));
        Assert.Equal(before, session[0]);
        session.RemoveView(0);
        Assert.Equal(secondKey, session[0].Places.ViewKey);
        Assert.Equal(Moon, session[0].ObserverCamera);
        var fullWorld = session.World;
        var epoch = session[0].Epoch;
        Assert.True(session.TryReplaceWorld(new(fullWorld.Ephemeris, [])));
        Assert.Equal(new[] { 0 }, session.SurfaceObserverFallbacks);
        Assert.Equal(Earth, session[0].ObserverCamera);
        Assert.Equal(Moon, session[0].Places.Find(SolarSystemSample.MoonId));
        Assert.Equal(secondKey, session[0].Places.ViewKey);
        Assert.Equal(epoch, session[0].Epoch);
        Assert.True(session.TryReplaceWorld(fullWorld));
        Assert.Equal(Earth, session[0].ObserverCamera);
        Assert.True(session.SetObserverBody(0, SolarSystemSample.MoonId));
        Assert.Equal(Moon, session[0].ObserverCamera);
    }

    [Fact]
    public void Scenario52RoundTripsInactivePlacesAndStableKeysWithoutChangingOldVersionGates()
    {
        var session = SharedClockTests.Create();
        session.SetEarthObserver(0, Earth);
        session.SetEarthObserver(0, Moon);
        byte[] bytes = ScenarioFile.Encode(session.CaptureScenario(0));
        var json = JsonNode.Parse(bytes)!;
        Assert.Equal(52, (int)json["version"]!);
        var restored = Assert.Single(ScenarioFile.Decode(bytes).Views);
        Assert.Equal(session[0], restored);
        Assert.Equal(session[0].Places.ViewKey, restored.Places.ViewKey);
        Assert.Equal(Earth, restored.CameraForBody(SolarSystemSample.EarthId));
        json["version"] = 51;
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
        var duplicate = session.CaptureScenario(0) with { Views = [session[0], session[0]] };
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Encode(duplicate));
        Assert.Throws<InvalidDataException>(() => session.RestoreScenario(duplicate));
    }

    [Fact]
    public void AutomaticPreferencesFollowKeysRatherThanIndexesAndDoNotRestoreTimeOrNavigation()
    {
        var session = SharedClockTests.Create(); session.Split();
        session.SetEarthObserver(0, Earth); session.SetEarthObserver(1, EarthObserverCamera.Tromso);
        var startup = session.CaptureScenario(0);
        byte[] bytes = ObserverPlacePreferences.Capture(session);
        session.SetEarthObserver(0, EarthObserverCamera.Tromso);
        session.Step(0, 7200);
        var epoch = session[0].Epoch;
        Assert.Equal(2, ObserverPlacePreferences.Apply(bytes, session));
        Assert.Equal(epoch, session[0].Epoch);
        Assert.Equal(Earth, session[0].ObserverCamera);
        session.RemoveView(0);
        Assert.Equal(1, ObserverPlacePreferences.Apply(bytes, session));
        Assert.Equal(EarthObserverCamera.Tromso, session[0].ObserverCamera);
        var fresh = SharedClockTests.Create();
        Assert.Equal(0, ObserverPlacePreferences.Apply(bytes, fresh));
        Assert.Equal(1, ObserverPlacePreferences.Apply(bytes, fresh, adoptSingleDefault: true));
        Assert.Equal(Earth, fresh[0].ObserverCamera);
        Assert.Equal(startup.Views[0].Places.ViewKey, fresh[0].Places.ViewKey);
        Assert.Equal(1, ObserverPlacePreferences.Apply(bytes, fresh, adoptSingleDefault: true));
    }

    [Fact]
    public void ExplicitPlacesAreNotOverwrittenByTheFreshProfileFallback()
    {
        var source = SharedClockTests.Create(); source.SetEarthObserver(0, Earth);
        var target = SharedClockTests.Create(); target.SetEarthObserver(0, Moon);
        Assert.Equal(0, ObserverPlacePreferences.Apply(ObserverPlacePreferences.Capture(source), target, true));
        Assert.Equal(Moon, target[0].ObserverCamera);
    }

    [Fact]
    public void AtomicPreferenceReplacementRespectsExistingReadersOnEachPlatform()
    {
        var root = Directory.CreateTempSubdirectory("st-place-pref-");
        try
        {
            var session = SharedClockTests.Create(); session.SetEarthObserver(0, Earth);
            string path = Path.Combine(root.FullName, "profile", "places.json");
            ObserverPlacePreferences.Save(path, session);
            byte[] before = ObserverPlacePreferences.Load(path);
            session.SetEarthObserver(0, EarthObserverCamera.Tromso);
            using (var reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (OperatingSystem.IsWindows())
                {
                    Assert.True(Record.Exception(() => ObserverPlacePreferences.Save(path, session)) is IOException or UnauthorizedAccessException);
                    Assert.Equal(before, ObserverPlacePreferences.Load(path));
                }
                else
                {
                    // Unix rename replaces the directory entry; the existing reader retains the old inode.
                    ObserverPlacePreferences.Save(path, session);
                    Assert.Equal(ObserverPlacePreferences.Capture(session), ObserverPlacePreferences.Load(path));
                }
                using var retained = new MemoryStream();
                reader.CopyTo(retained);
                Assert.Equal(before, retained.ToArray());
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            File.WriteAllBytes(path, new byte[ObserverPlacePreferences.MaximumBytes + 1]);
            Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Load(path));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void ABlockedDestinationKeepsTheLastValidPreferencesAndCleansTemporaryFiles()
    {
        var root = Directory.CreateTempSubdirectory("st-place-blocked-");
        try
        {
            var session = SharedClockTests.Create(); session.SetEarthObserver(0, Earth);
            string path = Path.Combine(root.FullName, "places.json"), backup = path + ".valid";
            ObserverPlacePreferences.Save(path, session);
            byte[] before = ObserverPlacePreferences.Load(path);
            File.Move(path, backup);
            Directory.CreateDirectory(path);
            session.SetEarthObserver(0, EarthObserverCamera.Tromso);
            Assert.True(Record.Exception(() => ObserverPlacePreferences.Save(path, session)) is IOException or UnauthorizedAccessException);
            Assert.Equal(before, ObserverPlacePreferences.Load(backup));
            Assert.Empty(Directory.GetFileSystemEntries(path));
            Assert.Empty(Directory.GetFiles(root.FullName, "*.tmp"));
            Directory.Delete(path);
            File.Move(backup, path);
            ObserverPlacePreferences.Save(path, session);
            Assert.Equal(ObserverPlacePreferences.Capture(session), ObserverPlacePreferences.Load(path));
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"views\":[]}")]
    [InlineData("{\"version\":1,\"views\":[null]}")]
    [InlineData("{\"version\":1,\"views\":[],\"unknown\":0}")]
    [InlineData("{")]
    public void CorruptPreferencesCannotMutateTheLiveSession(string json)
    {
        var session = SharedClockTests.Create(); var before = session[0];
        Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Apply(Encoding.UTF8.GetBytes(json), session, true));
        Assert.Equal(before, session[0]);
    }

    [Fact]
    public void PreferenceBoundsAndUtf8AreEnforcedBeforeApplyingAnything()
    {
        var session = SharedClockTests.Create();
        Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Apply([0xff], session));
        Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Apply(new byte[ObserverPlacePreferences.MaximumBytes + 1], session));
        var node = JsonNode.Parse(ObserverPlacePreferences.Capture(session))!;
        node["views"]!.AsArray().Add(node["views"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Apply(Encoding.UTF8.GetBytes(node.ToJsonString()), session));
    }

    [Fact]
    public void AnUnavailableSavedBodyFallsBackToItsOwnEarthPlaceWithoutDiscardingHistory()
    {
        var source = SharedClockTests.Create();
        source.SetEarthObserver(0, Earth); source.SetEarthObserver(0, Moon);
        var target = SharedClockTests.Create();
        Assert.Equal(1, ObserverPlacePreferences.Apply(ObserverPlacePreferences.Capture(source), target, true));
        Assert.Equal(Earth, target[0].ObserverCamera);
        Assert.Equal(Moon, target[0].Places.Find(SolarSystemSample.MoonId));
        var noEarth = source[0] with { Places = ObserverPlaceMemory.FromSaved(source[0].Places.ViewKey, [Moon]) };
        source.RestoreObserverPlaces(0, Moon, noEarth.Places);
        Assert.Equal(EarthObserverCamera.Default, source[0].ObserverCamera);
    }

    [Fact]
    public void OnlyTheHistoryFormatAllowsFilesAboveTheOldSizeLimit()
    {
        var session = SharedClockTests.Create();
        byte[] saved = ScenarioFile.Encode(session.CaptureScenario(0));
        byte[] padded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(saved).PadRight(40000));
        Assert.Single(ScenarioFile.Decode(padded).Views);
        var old = session.CaptureScenario(0) with { Views = [session[0] with { Places = default }] };
        byte[] oldPadded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ScenarioFile.Encode(old)).PadRight(40000));
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(oldPadded));
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(new byte[ScenarioFile.MaximumHistoryBytes + 1]));
        var node = JsonNode.Parse(saved)!;
        node["views"]!.AsArray().Add(node["views"]![0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData("key")] [InlineData("locations")] [InlineData("camera")] [InlineData("duplicate-field")]
    public void InvalidPreferenceEntriesAreRejectedAsAWhole(string defect)
    {
        var session = SharedClockTests.Create(); var before = session[0];
        var node = JsonNode.Parse(ObserverPlacePreferences.Capture(session))!;
        var entry = node["views"]![0]!;
        if (defect == "key") entry["key"] = Guid.Empty.ToString();
        if (defect == "locations") entry["locations"] = null;
        if (defect == "camera") entry["camera"]!["site"]!["latitudeDegrees"] = 91;
        string json = node.ToJsonString();
        if (defect == "duplicate-field") json = json.Replace("\"version\":1", "\"version\":1,\"version\":1");
        Assert.Throws<InvalidDataException>(() => ObserverPlacePreferences.Apply(Encoding.UTF8.GetBytes(json), session, true));
        Assert.Equal(before, session[0]);
    }

    private sealed class ModelFactAttribute : FactAttribute
    {
        public ModelFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES") ?? "", "pck00011.tpc")))
                Skip = "Set DE440 and the original planetary PCK directory for body-place switching.";
        }
    }
}
