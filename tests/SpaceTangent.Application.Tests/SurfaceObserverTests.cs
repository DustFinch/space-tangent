// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SurfaceObserverTests
{
    [SurfaceFact]
    [Trait("Category", "ExternalData")]
    public void RealBodyFramesAndTwoObserversSurviveScenarioReload()
    {
        var world = World();
        var epoch = TimeConversion.FromUtc(new(new(2024, 10, 18)), TimeScale.Tdb);
        foreach (var id in new[] { SolarSystemSample.MoonId, PlanetCatalogue.MarsId, new ObjectId("sol/titan") })
        {
            var camera = new EarthObserverCamera(new(0, 0, .1), 120, 20, true, true) { Body = id };
            var snapshot = world.Graph.Evaluate(epoch, DeltaTModel.MorrisonStephenson2004);
            Assert.True(SurfaceObserver.TryFrame(world, camera, snapshot, DeltaTModel.MorrisonStephenson2004, out var frame));
            Assert.True(snapshot.TryState(id, out var body));
            Assert.True(snapshot.TryOrientation(id, out var rotation));
            var fixedOffset = rotation.ToWorld.Transpose().Transform(frame.Observer.PositionKilometers - body.PositionKilometers);
            Assert.InRange((fixedOffset - new Vector3d(world.TargetRadiusKilometers(id) + .1, 0, 0)).Length, 0, 1e-5);
            Assert.InRange(frame.Direction(frame.LookDirection(120, 20))!.Value.AltitudeDegrees, 19.999999, 20.000001);
            Assert.True(SurfaceObserver.TryNavigation(world, camera, snapshot, DeltaTModel.FixedOffset, out var renderPose));
            Assert.True(renderPose.IsValid());
            var left = new ObserverView(epoch, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = camera };
            var right = new ObserverView(epoch.AddSeconds(3600), CameraMode.Surface, SolarSystemSample.MoonId, false)
                { EarthObserver = EarthObserverCamera.Moscow };
            var scenario = new SimulationScenario([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004);
            byte[] bytes = ScenarioFile.Encode(scenario);
            string text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("\"version\": 51", text);
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(text.Replace("\"version\": 51", "\"version\": 50"))));
            var session = new SimulationSession(world, epoch, DeltaTModel.FixedOffset);
            session.RestoreScenario(ScenarioFile.Decode(bytes));
            Assert.True(session.TryReplaceWorld(World()));
            Assert.Equal(scenario.Views.ToArray(), session.CaptureScenario(0).Views.ToArray());
            Assert.Equal(scenario.DeltaT, session.DeltaT);
            var imported = LegacyObserverProfiles.RestoreSurfaceBody(new(left with { EarthObserver = camera with { Body = null } }, scenario.DeltaT)
                { SiteBody = "Sol/" + (id == SolarSystemSample.MoonId ? "Earth/Moon" : id == PlanetCatalogue.MarsId ? "Mars" : "Saturn/Titan") }, world);
            Assert.Equal(CameraMode.Surface, imported.Mode);
            Assert.Equal(id, imported.EarthObserver!.Value.SurfaceBody);
            Assert.Null(imported.Navigation);
        }
        Assert.DoesNotContain(SurfaceObserver.Bodies(world), body => body.Id == SolarSystemSample.SunId || body.Id == new ObjectId("sol/phobos"));
    }

    [SurfaceFact]
    [Trait("Category", "ExternalData")]
    public void LunarChartsUseLunarGeometryAndUtcDatesInsteadOfAnEarthSite()
    {
        var observer = new ObserverView(TimeConversion.FromUtc(new(new(2024, 10, 18)), TimeScale.Tdb),
            CameraMode.Surface, SolarSystemSample.EarthId, false)
        {
            EarthObserver = new(new(0, 0, 0), 0, 0) { Body = SolarSystemSample.MoonId }
        };
        var result = ObserverCalculations.Calculate(World(), observer, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal(new HistoricalDate(2024, 10, 18), result.FirstDate);
        Assert.All(result.Days, day => Assert.InRange(day.Day.Maximum!.Value.AltitudeDegrees!.Value, 75, 90));
        Assert.All(result.MoonAngles, row => Assert.Null(row.MoonSeparationDegrees));
        Assert.Equal(366, result.Year.Count);
        Assert.InRange(result.Year.Count(row => row.VisibleAtNight == true), 100, 250);
    }

    [SurfaceFact]
    [Trait("Category", "ExternalData")]
    public void MissingBodyOrientationIsReportedInsteadOfUsingEarth()
    {
        var full = World();
        var empty = new SimulationWorld(full.Ephemeris, []);
        var epoch = TimeConversion.FromUtc(new(new(2024, 10, 18)), TimeScale.Tdb);
        var camera = EarthObserverCamera.Moscow with { Body = SolarSystemSample.MoonId };
        Assert.False(SurfaceObserver.TryFrame(empty, camera, empty.Graph.Evaluate(epoch), DeltaTModel.FixedOffset, out _));
        var observer = new ObserverView(epoch, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = camera };
        var session = new SimulationSession(empty, epoch, DeltaTModel.FixedOffset);
        Assert.Throws<InvalidDataException>(() => session.RestoreScenario(new([observer], false, 0, 0, DeltaTModel.FixedOffset)));
        Assert.Throws<ArgumentException>(() => ObserverEventFinder.Next(full, observer, DeltaTModel.FixedOffset));
    }

    private static SimulationWorld World() => new(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), [],
        planets: new PlanetEphemerides(Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES")!));
    private sealed class SurfaceFactAttribute : FactAttribute
    {
        public SurfaceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                !File.Exists(Path.Combine(Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES") ?? "", "pck00011.tpc")))
                Skip = "Set the real DE440 ephemeris and planetary PCK directory.";
        }
    }
}
