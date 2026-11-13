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
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPECULAR_MODEL_PROBE") is { Length: > 0 } model)
            {
                await ExerciseModelSpecular(directory, model);
                result = 0;
                return;
            }
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
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_NOTICE_PROBE") == "only")
            {
                await ExerciseTourNotices(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_MESH_SCALE_PROBE") == "1")
            {
                await ExerciseMissionMeshScale(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TIMED_BOUNDS_PROBE") == "1")
            {
                await ExerciseTimedGeometryBounds(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_OBSERVATION_PROBE") == "only")
            {
                await ExerciseMissionObservations(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_DISPLAY_PROBE") == "1")
            {
                await ExerciseMissionDisplay(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_FOLLOW_SPIN_PROBE") == "1")
            {
                await ExerciseFollowSpin(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_COMPOUND_MISSION_PROBE") == "1")
            {
                await ExerciseCompoundMission(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_LENS_GPU") is { Length: > 0 } lensRecording)
            {
                await ExerciseArchivedLens(scene, directory, lensRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_MODEL_MATERIAL") is { Length: > 0 } materialMode)
            {
                await ExerciseArchivedModelMaterial(scene, directory, materialMode);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_TIMED_MODEL") is { Length: > 0 } timedRecording)
            {
                await ExerciseArchivedTimedModel(scene, directory, timedRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_GEOMETRY_PROBE") == "only")
            {
                await ExerciseMissionGeometry(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_SENSOR_PROBE") == "only")
            {
                await ExerciseMissionSensors(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_SPICE_PROBE") == "only")
            {
                await ExerciseSampledSpice(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_PACKED_CASSINI") == "1")
            {
                await ExercisePackedCassini(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_PACKED_SCIENCE") == "only")
            {
                await CapturePackedScience(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROCEDURAL_TERRAIN_PROBE") == "only")
            {
                await ExerciseProceduralTerrain(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_GALACTIC_SKY_PROBE") == "only")
            {
                await ExerciseGalacticSky(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_REMOTE_HIPS_PROBE") == "1")
            {
                await ExerciseRemoteSkySurveys(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_SURVEY_PROBE") == "only")
            {
                await ExerciseLocalSkySurvey(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_ATMOSPHERES") is { Length: > 0 } recordings)
            {
                await ExerciseArchivedAtmospheres(scene, directory, recordings);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_STELLAR_MATERIAL_GPU") is { Length: > 0 } starRecording)
            {
                await ExerciseArchivedStellarMaterial(scene, directory, starRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_STAR_POINTS_GPU") is { Length: > 0 } starPointsRecording)
            {
                await ExerciseArchivedStarPoints(scene, directory, starPointsRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_BODY_GPU") == "only")
            {
                await ExerciseArchivedBodyGeometry(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_HUYGENS") is { Length: > 0 } huygens)
            {
                await ExerciseArchivedHuygens(scene, directory, huygens);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_ISS_MODEL") is { Length: > 0 } stationRecording)
            {
                await ExerciseArchivedStation(scene, directory, stationRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_RING_SHADOW") is { Length: > 0 } ringShadowRecording)
            {
                await ExerciseArchivedRingShadow(scene, directory, ringShadowRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_RINGS") is { Length: > 0 } ringRecordings)
            {
                await ExerciseArchivedRings(scene, directory, ringRecordings);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_COMET") is { Length: > 0 } cometRecording)
            {
                await ExerciseArchivedComets(scene, directory, cometRecording);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_SHADOWS") is { Length: > 0 } shadowRecording)
            {
                await ExerciseArchivedShadows(scene, directory, shadowRecording);
                result = 0;
                return;
            }
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_GPU") == "only")
            {
                await ExerciseMarsMoonModels(scene, epoch, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SSC_SPACECRAFT_PHOTOMETRY_PROBE") == "only")
            {
                await ExerciseStandardSpacecraft(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SATURN_ATMOSPHERE_PROBE") == "only")
            {
                await ExerciseSaturnRingAtmosphere(scene, epoch, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ECLIPSE_PROBE") == "only")
            {
                await ExerciseEclipses(scene, directory);
                await ExerciseTitanEclipse(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_COMPACT_LENS_PROBE") == "only")
            {
                await ExerciseCompactLens(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SURFACE_BODY_PROBE") == "only")
            {
                await ExerciseLegacySurfaceBody(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_DISABLED_PACKS_PROBE") is { Length: > 0 } packRoot)
            {
                await ExerciseLegacyDisabledPacks(scene, directory, packRoot);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_DISABLED_PLUGINS_PROBE") is { Length: > 0 } pluginManifest)
            {
                await ExerciseLegacyDisabledPlugins(scene, directory, pluginManifest);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_MARKINGS_PROBE") == "only")
            {
                await ExerciseSkyMarkings(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MINOR_BODY_PROBE") == "only")
            {
                await ExerciseMinorBodySwarm(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_DENSE_TLE_PROBE") == "1")
            {
                await ExerciseDenseTle(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TLE_PHOTOMETRY_PROBE") == "only")
            {
                await ExerciseTlePhotometry(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_EXTRASOLAR_PHOTOMETRY_PROBE") == "only")
            {
                await ExerciseExtrasolarSsc(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MOON_PHOTOMETRY_PROBE") == "only")
            {
                await ExerciseOuterMoonShapes(scene, epoch, directory);
                await ExerciseMoonPhotometry(scene, epoch, directory);
                await ExercisePlanetPhotometry(scene, epoch, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_METEOR_SETTINGS_PROBE") == "only")
            {
                await ExerciseMeteorRadiants(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_GROUND_PLANETS_PROBE") == "only")
            {
                await ExerciseGroundPlanets(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_EQUATORIAL_MOUNT_PROBE") == "only")
            {
                await ExerciseEquatorialMount(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_APPARENT_PLACES_PROBE") == "only")
            {
                await ExerciseApparentPlaces(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ZODIACAL_PROBE") == "only")
            {
                await ExerciseZodiacalLight(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_POINTER_COORDINATES_PROBE") == "only")
            {
                await ExercisePointerCoordinates(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_NIGHT_MODE_PROBE") == "only")
            {
                await ExerciseNightMode(scene, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CLOUD_SETTINGS_PROBE") == "only")
            {
                await ExercisePlanetAtmospheres(scene, epoch, directory);
                result = 0;
                return;
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_GRIDS_PROBE") == "only")
            {
                await ExerciseArchivedSkyGrids(scene, epoch, directory);
                result = 0;
                return;
            }
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
            await ExerciseTitanEclipse(scene, directory);
            await ExercisePlanets(scene, epoch, directory);
            await ExerciseSaturnRingAtmosphere(scene, epoch, directory);
            await ExerciseTileSampling(scene, directory);
            await ExerciseMarsMoonModels(scene, epoch, directory);
            await ExerciseOuterMoonShapes(scene, epoch, directory);
            await ExerciseMoonPhotometry(scene, epoch, directory);
            await ExerciseCatalogueMoons(scene, epoch, directory);
            await ExerciseDemoWorlds(scene, directory);
            await ExerciseProceduralTerrain(scene, directory);
            await ExerciseSmallBodies(scene, epoch, directory);
            await ExercisePlanetPhotometry(scene, epoch, directory);
            await ExerciseSmallBodyRings(scene, directory);
            await ExerciseExtrasolarSsc(scene, directory);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_SKY_SMOKE") == "1")
                await ExerciseStellarSpace(scene, epoch, directory);
            await ExerciseSkyCultures(scene, epoch, directory);
            await ExerciseArchivedSkyGrids(scene, epoch, directory);
            await ExerciseLocalSkySurvey(scene, directory);
            await ExerciseSkyPhotographs(scene, directory);
            await ExerciseCompactLens(scene, directory);
            await ExerciseTourNotices(scene, directory);
            await ExerciseRedshiftScript(scene, directory);
            await ExerciseArchivedBodyGeometry(scene, directory);
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
            await ExerciseMissionVectors(scene, directory);
            await ExerciseMissionAnnotations(scene, directory);
            await ExerciseMissionViewpoints(scene, directory);
            await ExerciseMissionTiledGlobe(scene, directory);
            await ExerciseMissionSwarms(scene, directory);
            await ExerciseLegacyModelFormats(scene, directory);
            await ExerciseStandardSpacecraft(scene, directory);
            await ExerciseCrossSourceSsc(scene, directory);
            await ExerciseSpiceSsc(scene, directory);
            await ExerciseGltfShuttle(scene, directory);
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
