// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe : Node
{
    public override async void _Ready()
    {
        int result = 1;
        try
        {
            string directory = System.Environment.GetEnvironmentVariable("SPACETANGENT_EARTH_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Set an output folder for the Earth orbit probe.");
            Directory.CreateDirectory(directory);
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!scene.InitializationFinished)
            {
                if (timer.Elapsed.TotalSeconds > 20)
                    throw new TimeoutException("The simulator did not initialize.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            var session = scene.Session ?? throw new InvalidOperationException("The probe needs DE440.");
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            if (session.World.Stellar.Stars.Count > 0)
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                const long mebibyte = 1024 * 1024;
                GD.Print($"Spatial catalogue: {session.World.Stellar.Stars.Count} entries; world ready in {timer.Elapsed.TotalSeconds:F2}s; working set {process.WorkingSet64 / mebibyte} MiB.");
            }
            SolarSystemSample sample = session.World.Ephemeris.Sample(epoch);
            Vector3d towardSun = (sample.Sun - sample.Earth).PositionKilometers;
            var sun = new Vector3((float)towardSun.X, (float)towardSun.Z, (float)-towardSun.Y).Normalized();
            Vector3 side = sun.Cross(Vector3.Up).Normalized();
            foreach (var (name, direction, range) in new[]
                     {
                         ("earth-day", (sun + side * .28f).Normalized(), 26_000d),
                         ("earth-terminator", (side + sun * .2f).Normalized(), 21_000d),
                         ("earth-night", (-sun + side * .42f).Normalized(), 22_000d),
                         ("earth-low-orbit", (side + sun * .65f).Normalized(), 6_710d),
                         ("earth-upper-atmosphere", (side + sun * .55f).Normalized(), 6_435d),
                         ("earth-reference-marks", (side + sun * .5f).Normalized(), 26_000d)
                     })
            {
                var pose = new NavigationCamera(sample.Earth.PositionKilometers, range, Math.Atan2(direction.X, direction.Z),
                    Math.Asin(direction.Y), SolarSystemSample.EarthId, true);
                var view = new ObserverView(epoch, CameraMode.Solar, SolarSystemSample.EarthId, false)
                {
                    Navigation = pose,
                    ReferenceMarks = name == "earth-reference-marks" ? BodyReferenceMarks.All : BodyReferenceMarks.None
                };
                var scenario = new SimulationScenario([view], false, 0, 0, DeltaTModel.MorrisonStephenson2004);
                string path = Path.Combine(directory, name + ".stscene");
                ScenarioFile.Save(path, scenario);
                if (!scene.LoadScenario(path) || !scene.Renderer(0).EarthOrbitAtmosphereActive)
                    throw new InvalidOperationException($"The Earth orbit material is unavailable in {name}.");
                if (name == "earth-reference-marks" && !scene.Renderer(0).ReferenceMarksVisible)
                    throw new InvalidOperationException("The saved Earth reference marks were not drawn.");
                if (DisplayServer.GetName() != "headless")
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var image = GetViewport().GetTexture().GetImage();
                    if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                        throw new IOException($"Could not capture {name}.");
                }
                GD.Print($"Earth orbit accepted camera: {name}, {range:F0} km from center.");
            }
            await ExercisePlanetAtmospheres(scene, epoch, directory);
            await ExerciseEclipses(scene, directory);
            await ExercisePlanets(scene, epoch, directory);
            await ExerciseTileSampling(scene, directory);
            await ExerciseMarsMoonModels(scene, epoch, directory);
            await ExerciseOuterMoonShapes(scene, epoch, directory);
            await ExerciseCatalogueMoons(scene, epoch, directory);
            await ExerciseDemoWorlds(scene, directory);
            await ExerciseProceduralTerrain(scene, directory);
            await ExerciseSmallBodies(scene, epoch, directory);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_SKY_SMOKE") == "1")
                await ExerciseStellarSpace(scene, epoch, directory);
            await ExerciseSkyCultures(scene, epoch, directory);
            await ExerciseLocalSkySurvey(scene, directory);
            await ExerciseSkyPhotographs(scene, directory);
            await ExerciseCompactLens(scene, directory);
            await ExerciseTourNotices(scene, directory);
            await CaptureShip(scene, directory);
            await ExerciseHistoricalEpoch(scene, directory);
            await ExerciseHistoricalSurface(scene, directory);
            await ExerciseGroundSky(scene, directory);
            await ExerciseMeteorRadiants(scene, directory);
            await ExercisePanoramas(scene, directory);
            await ExerciseOptics(scene, directory);
            await ExerciseFinders(scene, directory);
            await ExerciseHistoricalStars(scene, directory);
            await CaptureSurfaceSite(scene, epoch, directory);
            await CapturePackedScience(scene, directory);
            await ExerciseMissionGeometry(scene, directory);
            await ExerciseMissionSensors(scene, directory);
            await ExerciseMissionObservations(scene, directory);
            await ExerciseSampledSpice(scene, directory);
            await ExerciseNativeMissionAttitude(scene, directory);
            await ExerciseMissionAxes(scene, directory);
            await ExerciseLegacyModelFormats(scene, directory);
            await ExerciseMinorMoons(scene, directory);
            await ExerciseMissionGlobes(scene, directory);
            await ExerciseMissionParticles(scene, directory);
            await ExerciseSourceFactories(scene, directory);
            await ExercisePackedFactories(scene, directory);
            await ExerciseGalacticSky(scene, directory);
            await ExerciseRecording(scene, directory);
            result = 0;
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
        }
        finally
        {
            GetTree().Quit(result);
        }
    }
}
