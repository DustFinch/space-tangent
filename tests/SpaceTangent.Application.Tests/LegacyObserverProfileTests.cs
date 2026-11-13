// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyObserverProfileTests
{
    [ArchiveSettingsFact]
    public void ImportsTheArchivedMoscowProfileAndKeepsTheExistingView()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SETTINGS")!;
        byte[] original = File.ReadAllBytes(path);
        var now = JulianInstant.FromJulianDate(2455755.0, TimeScale.Tdb);
        var satellite = new ObjectId("norad/26997");
        var view = new ObserverView(now, CameraMode.Solar, satellite, false)
        {
            Navigation = new(new Vector3d(1, 2, 3), 42, .12, .2, satellite, true),
            Sky = new(true, false, true, true),
            EarthObserver = EarthObserverCamera.Tromso with
            {
                Conditions = new GroundSkyConditions(9, -10, 900), LandscapeId = "trees"
            }
        };
        var result = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
        var observer = result.Observer;
        Assert.Equal(now, observer.Epoch);
        Assert.Equal(view.Mode, observer.Mode);
        Assert.Equal(view.Selection, observer.Selection);
        Assert.Equal(view.Navigation, observer.Navigation);
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, result.DeltaT);
        Assert.Equal(50, observer.FieldOfViewDegrees);
        Assert.Equal(new CameraAutoOrbit(120, false), observer.AutoOrbit);
        Assert.True(observer.CameraInertia);
        Assert.True(observer.Sky.Stars && observer.Sky.DeepSky && observer.Sky.Labels && observer.Sky.Meteors);
        Assert.False(observer.Sky.EquatorialGrid || observer.Sky.Constellations);
        var camera = Assert.IsType<EarthObserverCamera>(observer.EarthObserver);
        Assert.Equal(new EarthSite(55.7558, 37.6173, .156), camera.Site);
        Assert.True(camera.CardinalPoints && camera.HorizonGrid);
        Assert.Null(camera.LandscapeId);
        var conditions = Assert.IsType<GroundSkyConditions>(camera.Conditions);
        Assert.Equal(4, conditions.Bortle);
        Assert.Equal(10, conditions.TemperatureCelsius);
        Assert.Null(conditions.PressureHectopascals);
        Assert.False(conditions.Scintillation);
        Assert.True(conditions.Refraction);
        Assert.NotNull(observer.Optics);
        Assert.False(observer.Optics.Active);
        Assert.True(observer.Optics.Profile.Inverted);
        Assert.Equal(10, observer.Optics.Profile.Eyepiece.FocalMillimeters);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ArchivedOpticsIndicesCountDuplicatesAndAcceptPartialSettings()
    {
        var directory = Directory.CreateTempSubdirectory("st-optics-");
        try
        {
            string path = Path.Combine(directory.FullName, "settings-v1.json");
            string library = Path.Combine(directory.FullName, "optics-v1.json");
            File.WriteAllText(library, """
                {
                  "Telescopes": [
                    {"Name":"First","Kind":"Reflector","ApertureMillimeters":100,"FocalMillimeters":800},
                    {"Name":"First","Kind":"Reflector","ApertureMillimeters":100,"FocalMillimeters":800},
                    {"Name":"Last","Kind":"Refractor","ApertureMillimeters":110,"FocalMillimeters":990}
                  ],
                  "Eyepieces": [
                    {"Name":"First lens","FocalMillimeters":20,"ApparentFieldDegrees":65},
                    {"Name":"First lens","FocalMillimeters":20,"ApparentFieldDegrees":65},
                    {"Name":"Last lens","FocalMillimeters":18,"ApparentFieldDegrees":68}
                  ]
                }
                """);
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Telescope":5,"Eyepiece":6,"Barlow":2}""");
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Surface, null, false);
            var initial = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
            Assert.Equal("Last", initial.Observer.Optics!.Profile.Tube.Name);
            Assert.Equal("Last lens", initial.Observer.Optics.Profile.Eyepiece.Name);
            Assert.Equal(165, initial.Observer.Optics.Profile.Magnification());
            Assert.False(initial.Observer.Optics.Active);
            Assert.Equal(5, ObserverOpticsLibrary.Load(library).Tubes.Count);

            var active = initial.Observer with { Optics = initial.Observer.Optics with { Active = true } };
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Seeing":3.5,"InvertEyepiece":false}""");
            var modified = LegacyObserverProfiles.Read(path, active, initial.DeltaT).Observer;
            Assert.Equal(active.Optics with { Profile = active.Optics.Profile with { SeeingArcseconds = 3.5, Inverted = false } },
                modified.Optics);

            File.WriteAllText(library, "{");
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Telescope":1}""");
            var restored = LegacyObserverProfiles.Read(path, modified, initial.DeltaT).Observer;
            Assert.Equal("Refractor 80/480", restored.Optics!.Profile.Tube.Name);
            Assert.Equal("Last lens", restored.Optics.Profile.Eyepiece.Name);
            Assert.True(restored.Optics.Active);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void ArchivedOrbitRateAndDirectionSurviveScenarioRoundTrip()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","AutoRotateSeconds":45,"AutoRotateClockwise":true}""");
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Solar, null, false);
            var profile = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
            Assert.Equal(new CameraAutoOrbit(45, true), profile.Observer.AutoOrbit);

            var active = profile.Observer with { AutoOrbit = new(45, true, true) };
            var scenario = new SimulationScenario([active, view], false, 0, 0, profile.DeltaT);
            ScenarioFile.Save(path, scenario);
            Assert.Equal(scenario.Views, ScenarioFile.Load(path).Views);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(20, json["version"]!.GetValue<int>());
            json["version"] = 19;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchivedMouseSensitivityAppliesToOneViewAndRoundTrips()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","MouseSensitivity":2.5}""");
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Solar, null, false);
            var profile = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
            Assert.Equal(2.5, profile.Observer.MouseSensitivity);

            var scene = new SimulationScenario([profile.Observer, view], false, 0, 0, profile.DeltaT);
            ScenarioFile.Save(path, scene);
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(21, json["version"]!.GetValue<int>());
            json["version"] = 20;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));

            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","MouseSensitivity":6}""");
            Assert.Equal(2.5, LegacyObserverProfiles.Read(path, profile.Observer, profile.DeltaT).Observer.MouseSensitivity);
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","MouseSensitivity":1}""");
            Assert.Null(LegacyObserverProfiles.Read(path, profile.Observer, profile.DeltaT).Observer.MouseSensitivity);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchivedCameraInertiaRoundTripsForOnlyOneView()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Inertia":true}""");
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Solar, null, false);
            var profile = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
            Assert.True(profile.Observer.CameraInertia);
            var scene = new SimulationScenario([profile.Observer, view], false, 0, 0, profile.DeltaT);
            ScenarioFile.Save(path, scene);
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(22, json["version"]!.GetValue<int>());
            json["version"] = 21;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchivedLandscapeFogRoundTripsAndPreservesDefaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"Site":{},"Landscape":"trees","LandscapeFog":false}""");
            var defaultView = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Surface, null, false)
            {
                EarthObserver = EarthObserverCamera.Moscow
            };
            var archived = LegacyObserverProfiles.Read(path, defaultView, DeltaTModel.EspenakMeeus2006);
            Assert.True(archived.Observer.EarthObserver?.HideLandscapeFog);
            var scene = new SimulationScenario([archived.Observer, defaultView], false, 0, 0, archived.DeltaT);
            ScenarioFile.Save(path, scene);
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(23, json["version"]!.GetValue<int>());
            json["version"] = 22;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));

            File.WriteAllText(path, """{"Site":{}}""");
            Assert.True(LegacyObserverProfiles.Read(path, archived.Observer, archived.DeltaT).Observer.EarthObserver?.HideLandscapeFog);
            File.WriteAllText(path, """{"Site":{},"LandscapeFog":true}""");
            Assert.False(LegacyObserverProfiles.Read(path, archived.Observer, archived.DeltaT).Observer.EarthObserver?.HideLandscapeFog);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ArchivedSatelliteVisibilityRoundTripsForOnlyOneView()
    {
        string path = Path.GetTempFileName();
        try
        {
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Solar, null, false);
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Satellites":false}""");
            var archived = LegacyObserverProfiles.Read(path, view, DeltaTModel.MorrisonStephenson2004);
            Assert.True(archived.Observer.Sky.HideSatellites);

            var scenario = new SimulationScenario([view, archived.Observer], false, 0, 0, archived.DeltaT);
            ScenarioFile.Save(path, scenario);
            Assert.Equal(scenario.Views, ScenarioFile.Load(path).Views);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(28, json["version"]!.GetValue<int>());
            json["version"] = 27;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));

            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Satellites":true}""");
            Assert.False(LegacyObserverProfiles.Read(path, archived.Observer, archived.DeltaT).Observer.Sky.HideSatellites);
        }
        finally { File.Delete(path); }
    }

    private sealed class ArchiveSettingsFactAttribute : FactAttribute
    {
        public ArchiveSettingsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SETTINGS")))
                Skip = "Set the path to the archived settings document.";
        }
    }
}
