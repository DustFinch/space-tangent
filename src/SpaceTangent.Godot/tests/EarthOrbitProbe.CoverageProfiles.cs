// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task<bool> ExerciseCoverageProfile(SolarSystemScene scene, string directory)
    {
        string? profile = System.Environment.GetEnvironmentVariable("SPACETANGENT_COVERAGE_PROFILE");
        if (profile is null) return false;
        switch (profile)
        {
            case "planets":
                await ExercisePlanetAtmospheres(scene, SurfaceProjection.FullMoonEpoch, directory);
                await ExerciseEclipses(scene, directory);
                await ExerciseTitanEclipse(scene, directory);
                await ExercisePlanets(scene, SurfaceProjection.FullMoonEpoch, directory);
                await ExerciseSaturnRingAtmosphere(scene, SurfaceProjection.FullMoonEpoch, directory);
                break;
            case "demo-worlds":
                RequireControl("SPACETANGENT_DEMO_WORLDS_PROBE");
                RequireControl("SPACETANGENT_PROCEDURAL_TERRAIN_PROBE");
                await ExerciseDemoWorlds(scene, directory);
                await ExerciseProceduralTerrain(scene, directory);
                break;
            case "recording":
                RequireControl("SPACETANGENT_RECORDING_PROBE");
                await ExerciseRecording(scene, directory);
                break;
            case "compact-lens":
                RequireControl("SPACETANGENT_COMPACT_LENS_PROBE");
                await ExerciseCompactLens(scene, directory);
                break;
            case "observing":
                RequireControl("SPACETANGENT_OPTICAL_FINDERS_PROBE");
                RequireInput("SPACETANGENT_EARTH_PLACES");
                await ExerciseGroundSky(scene, directory);
                await ExerciseOptics(scene, directory);
                await ExerciseFinders(scene, directory);
                await ExerciseApparentPlaces(scene, directory);
                await ExerciseEquatorialMount(scene, directory);
                await ExerciseNightMode(scene, directory);
                break;
            case "mission-geometry":
                RequireControl("SPACETANGENT_MISSION_GEOMETRY_PROBE");
                RequireControl("SPACETANGENT_MISSION_SENSOR_PROBE");
                RequireInput("SPACETANGENT_SAMPLED_MISSIONS_ROOT");
                await ExerciseMissionGeometry(scene, directory);
                await ExerciseMissionSensors(scene, directory);
                break;
            case "swarm-particles":
                RequireControl("SPACETANGENT_MISSION_SWARM_GPU");
                RequireInput("SPACETANGENT_SWARM_INSTALL");
                RequireInput("SPACETANGENT_PARTICLE_GPU_ROOT");
                await ExerciseMissionSwarms(scene, directory);
                await ExerciseMissionParticles(scene, directory);
                break;
            case "stellar":
                RequireInput("SPACETANGENT_STELLAR_ARCHIVE");
                await ExerciseStellarSpace(scene, SurfaceProjection.FullMoonEpoch, directory);
                break;
            case "ssc-bodies":
                RequireControl("SPACETANGENT_MINOR_MOONS_GPU");
                RequireInput("SPACETANGENT_MINOR_MOONS");
                RequireInput("SPACETANGENT_SMALL_RINGS");
                RequireInput("SPACETANGENT_PROBE_SCRIPTED_SSC");
                await ExerciseSmallBodyRings(scene, directory);
                await ExerciseMinorMoons(scene, directory);
                await ExerciseSourceFactories(scene, directory);
                break;
            case "archive-wire":
                await ExerciseArchivedWire(scene, directory);
                break;
            case "landscapes":
                RequireInput("SPACETANGENT_LANDSCAPES");
                await ExercisePanoramas(scene, directory);
                break;
            case "sky-cultures":
                RequireControl("SPACETANGENT_SKY_CULTURE_GPU");
                RequireInput("SPACETANGENT_SKY_CULTURES");
                await ExerciseSkyCultures(scene, SurfaceProjection.FullMoonEpoch, directory);
                break;
            case "sky-grids":
                await ExerciseArchivedSkyGrids(scene, SurfaceProjection.FullMoonEpoch, directory);
                break;
            case "sky-assets":
                await ExerciseAuthoredSkyAssets(scene, directory);
                break;
            case "surface-tiles":
                RequireInput("SPACETANGENT_SURFACE_TILES");
                RequireInput("SPACETANGENT_TILE_FIXTURE");
                await ExerciseTileSampling(scene, directory);
                break;
            default:
                throw new InvalidOperationException($"Unknown coverage profile: {profile}.");
        }
        global::Godot.GD.Print($"SpaceTangent Earth coverage profile passed: {profile}");
        return true;

        static void RequireControl(string variable)
        {
            if (System.Environment.GetEnvironmentVariable(variable) != "1")
                throw new InvalidOperationException($"Coverage profile requires {variable}=1.");
        }

        static void RequireInput(string variable)
        {
            string? path = System.Environment.GetEnvironmentVariable(variable);
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new InvalidOperationException($"Coverage profile requires an existing {variable} input.");
        }
    }
}
