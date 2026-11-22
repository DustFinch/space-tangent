// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class GreenwichDefaultTests
{
    [Fact]
    public void InitialObserverHasOneExplicitGreenwichSiteAndFixedUtcClock()
    {
        Assert.Equal(EarthObserverCamera.Greenwich, EarthObserverCamera.Default);
        var camera = EarthObserverCamera.Default;
        Assert.Equal(new EarthSite(51.4772, 0, 0), camera.Site);
        Assert.True(camera.IsEarth && camera.IsValid());
        Assert.Equal(new ObserverSiteClock("Greenwich", "Etc/UTC", 0), camera.CivilClock);
        foreach (var date in new[] { new DateOnly(2024, 1, 1), new DateOnly(2024, 7, 1) })
        {
            var epoch = TimeConversion.FromUtc(new(date, 23, 30));
            Assert.Equal(date, camera.CivilClock!.Value.DateAt(epoch, camera.Site, DeltaTModel.FixedOffset));
            Assert.InRange(Math.Abs((double)camera.CivilClock.Value.At(date, new(23, 30), camera.Site,
                DeltaTModel.FixedOffset).DifferenceNanoseconds(TimeConversion.Convert(epoch, TimeScale.Tdb))), 0, 1000);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void OldScenesWithoutASiteUseTheNeutralDefaultWithoutInventingCoordinates(int version)
    {
        string json = $$"""
            {"version":{{version}},"views":[{"dayTdb":2451545,"nanosecondsOfDay":0,"camera":"surface","selection":null,"playing":false}],
             "synchronized":false,"clockOwner":0,"activeView":0,"deltaT":"fixedOffset"}
            """;
        var observer = Assert.Single(ScenarioFile.Decode(Encoding.UTF8.GetBytes(json)).Views);
        Assert.Null(observer.EarthObserver);
        Assert.Equal(EarthObserverCamera.Greenwich, observer.EarthObserver ?? EarthObserverCamera.Default);
    }

    [Fact]
    public void AnExplicitUserSiteRetainsItsCoordinatesClockAndDirection()
    {
        var chosen = EarthObserverCamera.Moscow with { CivilClock = new("Chosen site", "Europe/Moscow", 3) };
        var scenario = new SimulationScenario([new(new(2451545, 0, TimeScale.Tdb), CameraMode.Surface, null, false)
            { EarthObserver = chosen }], false, 0, 0, DeltaTModel.FixedOffset);
        Assert.Equal(chosen, Assert.Single(ScenarioFile.Decode(ScenarioFile.Encode(scenario)).Views).EarthObserver);
    }

    [ModelFact]
    public void GreenwichMoonPresetPointsAtTheIndependentGeometricReference()
    {
        // CSPICE 8.4.0/DE440s + ERFA 2.0.1.5 c2t06a; TT-UT1=69.184 s, geometric (no light-time/refraction).
        // DE440s SHA256 c1c7feeab882263fc493a9d5a5b2ddd71b54826cdf65d8d17a76126b260a49f2.
        var camera = EarthObserverCamera.GreenwichFullMoon;
        var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var light = EarthSurfaceLighting.Sample(ephemeris.Sample(epoch), camera.Site, DeltaTModel.FixedOffset);
        var ray = light.EarthToMoon - light.PositionKilometers;
        double azimuth = Math.Atan2(Vector3d.Dot(ray, light.EastNorthUp.X), Vector3d.Dot(ray, light.EastNorthUp.Y)) * 180 / Math.PI;
        if (azimuth < 0) azimuth += 360;
        Assert.InRange(Math.Abs(azimuth - 194.7474431290764), 0, .001);
        Assert.InRange(Math.Abs(light.LunarAltitudeRadians * 180 / Math.PI - 44.67997698077449), 0, .001);
        Assert.InRange(Math.Abs(camera.AzimuthDegrees - azimuth), 0, .001);
        Assert.InRange(Math.Abs(camera.ElevationDegrees - light.LunarAltitudeRadians * 180 / Math.PI), 0, .001);
        Assert.True(light.SolarAltitudeRadians * 180 / Math.PI < -40 && light.RelativeLunarFlux > .9);
    }

    [Fact]
    public void ImportingAnUnspecifiedSiteKeepsTheNewUtcClockAndItsVersionGate()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"Site":{}}""");
            var view = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Surface, null, false);
            var imported = LegacyObserverProfiles.Read(path, view, DeltaTModel.FixedOffset).Observer;
            Assert.Equal(EarthObserverCamera.Greenwich.Site, imported.EarthObserver!.Value.Site);
            Assert.Equal(EarthObserverCamera.Greenwich.CivilClock, imported.EarthObserver.Value.CivilClock);
            string json = Encoding.UTF8.GetString(ScenarioFile.Encode(new([imported], false, 0, 0, DeltaTModel.FixedOffset)));
            Assert.Contains("\"version\": 48", json);
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(json.Replace("\"version\": 48", "\"version\": 47"))));
        }
        finally { File.Delete(path); }
    }

    private sealed class ModelFactAttribute : FactAttribute
    {
        public ModelFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the pinned DE440 model to verify the Greenwich Moon direction.";
        }
    }
}
