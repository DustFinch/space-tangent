// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class MissionReferenceBookmarkTests
{
    [OriginalMissionFact]
    [Trait("Category", "ExternalData")]
    public void RestoresTheSelectedReferenceArcAcrossTheRealMslLanding()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_MSL_ROOT")!;
        var missions = SampledMissionCatalog.Load("msl.json", root, name => name switch
        {
            "Sun" => SolarSystemSample.SunId,
            "Mars" => PlanetCatalogue.MarsId,
            _ => null
        });
        var mission = Assert.Single(missions, item => item.Name == "MSL");
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var planets = new PlanetEphemerides(root, Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_PCK"));
        var world = new SimulationWorld(ephemeris, [], planets: planets, sampledMissions: missions);
        JulianInstant landing = mission.Arcs[1].Start;
        JulianInstant before = landing.AddSeconds(-60);
        const DeltaTModel deltaT = DeltaTModel.MorrisonStephenson2004;

        string folder = Path.Combine(Path.GetTempPath(), "Рамки " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "Избранное.json");
            File.WriteAllText(source, JsonSerializer.Serialize(new[] { Entry("Перелёт", before), Entry("Посадка", landing.AddSeconds(1)) }));
            var favorites = new ObserverBookmarkFile(Path.Combine(folder, "bookmarks.json"));
            var imported = favorites.ImportFavorites(source, [], world, deltaT);
            Assert.Equal((2, 0), (imported.Added, imported.Skipped));
            ObserverView cruise = Read(0);
            ObserverView surface = Read(1);
            Assert.Equal(NavigationFrame.Catalogue, cruise.Navigation!.Value.Placement!.Value.Frame);
            Assert.Equal(new CatalogueCameraFrame(JulianInstant.FromJulianDate(before.ToJulianDate(), TimeScale.Tdb), true),
                cruise.Navigation.Value.Placement.Value.CatalogueFrame);
            Assert.Equal(mission.Id, surface.Navigation!.Value.Target);

            var session = new SimulationSession(world, landing, deltaT);
            session.RestoreScenario(new([cruise, surface], false, 0, 0, deltaT));
            JulianInstant next = landing.AddSeconds(1200);
            session.Seek(0, next);
            var snapshot = world.Graph.Evaluate(next);
            session.UpdateCamera(0, snapshot, 0);
            Assert.Equal(surface, session[1]);
            session.Seek(1, next);
            session.UpdateCamera(1, snapshot, 0);
            var solarCamera = session[0].Navigation!.Value;
            var marsCamera = session[1].Navigation!.Value;
            Assert.True(snapshot.TryState(SolarSystemSample.SunId, out var sun));
            Assert.True(snapshot.TryState(PlanetCatalogue.MarsId, out var mars));
            Assert.Equal(sun.PositionKilometers, solarCamera.AnchorKilometers);
            Assert.Equal(mars.PositionKilometers, marsCamera.AnchorKilometers);
            Assert.True((surface.Navigation.Value.ResolvePlacement(world.Graph.Evaluate(landing), deltaT).Forward -
                marsCamera.ResolvePlacement(snapshot, deltaT).Forward).Length > .001);
            string scenario = Path.Combine(folder, "Два вида.stscene");
            ScenarioFile.Save(scenario, session.CaptureScenario(0));
            var loaded = ScenarioFile.Load(scenario);
            Assert.Equal(solarCamera, loaded.Views[0].Navigation);
            Assert.Equal(marsCamera, loaded.Views[1].Navigation);
            Assert.Equal(deltaT, loaded.DeltaT);

            ObserverView Read(int index) => SceneLink.Read(favorites.Load()[index].Link, world, deltaT).Scenario.Views.Single();
        }
        finally { Directory.Delete(folder, recursive: true); }

        object Entry(string name, JulianInstant selection) => new
        {
            Name = name, SchemaVersion = 3, Frame = 6, Rate = 0,
            CatalogFrameOwner = mission.Id.Value, CatalogFrameOrbit = true, CatalogFrameSelectionJd = selection.ToJulianDate(),
            Observer = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, frame = "J2000EclipticYUp",
                positionConvention = "universal-native-J2000-ecliptic-micro-light-year",
                orientationConvention = "active-camera-to-frame-wxyz", selectedId = mission.Id.Value,
                time = new
                {
                    wholeDays = landing.Day.ToString(CultureInfo.InvariantCulture),
                    nanosecondsOfDay = landing.NanosecondsOfDay.ToString(CultureInfo.InvariantCulture), scale = "Tdb"
                },
                positionBits = new { x = ZeroPosition, y = ZeroPosition, z = ZeroPosition },
                orientationBinary64 = new { x = ZeroRotation, y = ZeroRotation, z = ZeroRotation, w = "3ff0000000000000" }
            })
        };
    }

    private const string ZeroPosition = "00000000000000000000000000000000";
    private const string ZeroRotation = "0000000000000000";

    private sealed class OriginalMissionFactAttribute : FactAttribute
    {
        public OriginalMissionFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MSL_ROOT")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_PCK")))
                Skip = "Set the original MSL catalogue and DE440 paths.";
        }
    }
}
