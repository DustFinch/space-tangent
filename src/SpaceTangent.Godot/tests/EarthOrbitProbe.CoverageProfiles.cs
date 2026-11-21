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
    }
}
