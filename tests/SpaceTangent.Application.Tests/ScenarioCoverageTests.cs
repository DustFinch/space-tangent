// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ScenarioCoverageTests
{
    private static readonly ObserverView Base = new(JulianInstant.FromJulianDate(2451545, TimeScale.Tdb), CameraMode.Solar, null, false);

    [Fact]
    public void EveryWritableViewFieldAndNestedSkyOptionRoundTrips()
    {
        var values = new Dictionary<string, object?>
        {
            ["Epoch"] = Base.Epoch.AddSeconds(1), ["Mode"] = CameraMode.Surface,
            ["Selection"] = new ObjectId("sol/moon"), ["Playing"] = true,
            ["Terrain"] = LunarTerrainCamera.Copernicus,
            ["Navigation"] = new NavigationCamera(new(1, 2, 3), 100, .2, .3, null, false),
            ["ScriptAimTarget"] = new ObjectId("sol/earth"), ["EarthObserver"] = FullEarth(),
            ["Sky"] = new SkyLayerSettings(true, true, true, true), ["ReferenceMarks"] = BodyReferenceMarks.All,
            ["FieldOfViewDegrees"] = 30d, ["AutoOrbit"] = new CameraAutoOrbit(120, true, true),
            ["MouseSensitivity"] = 2d, ["CameraInertia"] = true, ["PlaybackRate"] = -600d,
            ["LightTimeDelayed"] = true, ["LightTimeOffsetNanoseconds"] = 123456789L,
            ["Optics"] = new ObserverOptics(ObserverOpticsLibrary.Default with { Barlow = 2, SeeingArcseconds = 3,
                Inverted = false, HoldSelection = false }, true) { Finders = new(true, CameraSensorPreset.FullFrame, true) }
        };
        foreach (PropertyInfo property in Writable(typeof(ObserverView)))
        {
            Assert.True(values.ContainsKey(property.Name), $"Provide a round-trip case for new view field {property.Name}.");
            object boxed = property.Name == "LightTimeOffsetNanoseconds" ? Base with { LightTimeDelayed = true } : Base;
            property.SetValue(boxed, values[property.Name]);
            Check((ObserverView)boxed, property.Name);
        }
        foreach (PropertyInfo property in Writable(typeof(SkyLayerSettings)))
        {
            object[] cases = Cases(property);
            Assert.NotEmpty(cases);
            foreach (object value in cases)
            {
                object boxed = default(SkyLayerSettings);
                property.SetValue(boxed, value);
                Check(Base with { Sky = (SkyLayerSettings)boxed }, "Sky." + property.Name);
            }
        }
        Check(Base with { EarthObserver = FullEarth(), Optics = (ObserverOptics)values["Optics"]! }, "nested settings");
    }

    [Fact]
    public void AllPublishedVersionsAcceptMinimalScenesAndNestedLayoutsRoundTrip()
    {
        var minimal = new SimulationScenario([Base], false, 0, 0, DeltaTModel.FixedOffset);
        var json = JsonNode.Parse(ScenarioFile.Encode(minimal))!.AsObject();
        for (int version = 1; version <= 50; version++)
        {
            json["version"] = version;
            Assert.Equal(Base, ScenarioFile.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())).Views[0]);
        }
        var session = SharedClockTests.Create();
        session.Split();
        session.SplitView(1, ViewSplit.Stacked);
        session.ResizeViewDivider("", .3);
        var before = session.CaptureScenario(2);
        var after = ScenarioFile.Decode(ScenarioFile.Encode(before));
        Assert.Equal(before.Layout, after.Layout);
        Assert.Equal(before.Views.ToArray(), after.Views.ToArray());
    }

    [Fact]
    public void InvalidRestoresAndWorldReplacementLeaveLiveStateUntouched()
    {
        var session = SharedClockTests.Create();
        session.Split();
        session.Synchronize(false, 0);
        session.Step(1, 3600);
        var before = session.CaptureScenario(1);
        foreach (var bad in new[] {
            session[0] with { Sky = new() { SurveyBrightness = 99 } },
            session[0] with { AutoOrbit = new(0, false) },
            session[0] with { MouseSensitivity = -1 },
            session[0] with { Selection = new ObjectId("missing/object") },
            session[0] with { LightTimeOffsetNanoseconds = -1 } })
        {
            Assert.Throws<InvalidDataException>(() => session.RestoreScenario(before with { Views = [bad, session[1]] }));
            Assert.Equal(before.Views.ToArray(), session.CaptureScenario(1).Views.ToArray());
        }
        var world = session.World;
        Assert.False(session.TryReplaceWorld(new(new NarrowEphemeris(), [])));
        Assert.Same(world, session.World);
        Assert.Equal(before.Views.ToArray(), session.CaptureScenario(1).Views.ToArray());
        byte[] valid = ScenarioFile.Encode(before);
        foreach (string invalid in new[] {
            Encoding.UTF8.GetString(valid).Replace("\"version\": 7", "\"version\": 999"),
            Encoding.UTF8.GetString(valid).Replace("\"version\": 7", "\"version\": 7, \"version\": 7"),
            Encoding.UTF8.GetString(valid).Replace("\"camera\": \"solar\"", "\"camera\": \"unknown\""),
            "{}" })
        {
            Exception? error = Record.Exception(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(invalid)));
            Assert.True(error is InvalidDataException or JsonException, $"Expected a data error, got {error?.GetType().Name ?? "success"}.");
        }
    }

    private static IEnumerable<PropertyInfo> Writable(Type type) => type.GetProperties().Where(p => p.SetMethod is not null);
    private static void Check(ObserverView view, string field)
    {
        var decoded = ScenarioFile.Decode(ScenarioFile.Encode(new([view], false, 0, 0, DeltaTModel.FixedOffset))).Views[0];
        Assert.True(JsonSerializer.Serialize(view) == JsonSerializer.Serialize(decoded), $"Field did not round-trip: {field}");
    }

    private static object[] Cases(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(bool)) return [true, false];
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().ToArray();
        if (type == typeof(string)) return [property.Name == "SurveyId" ? "dss" : "western"];
        if (type == typeof(double)) return [1d];
        if (type == typeof(int)) return [10];
        if (type == typeof(DeepSkyVisibility)) return [new DeepSkyVisibility(7, "M,NGC")];
        throw new InvalidOperationException($"Provide nested settings coverage for {property.Name}.");
    }

    private static EarthObserverCamera FullEarth() => EarthObserverCamera.Tromso with
    {
        HorizonGrid = true, CardinalPoints = true, HideLandscapeFog = true, EquatorialMount = true,
        MoonScale = 2, PlanetScale = 3, ApparentPlaces = true, LandscapeId = "city",
        CivilClock = new("Test", "UTC", 0), Conditions = new(5, 12, 900, .3, false, false)
    };

    private sealed class NarrowEphemeris : SpaceTangent.Core.Ephemerides.ISolarSystemEphemeris
    {
        public JulianInstant Start => Base.Epoch.AddSeconds(10000);
        public JulianInstant End => Start.AddSeconds(1000);
        public SpaceTangent.Core.Ephemerides.SolarSystemSample Sample(JulianInstant epoch) => new SharedClockTests.StaticEphemeris().Sample(epoch);
    }
}
