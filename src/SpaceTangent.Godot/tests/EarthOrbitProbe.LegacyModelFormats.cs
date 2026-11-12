// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseLegacyModelFormats(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_MODEL_FORMATS_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_MODEL_FORMATS") is not { Length: > 0 } root)
            return;
        const string input = "SPACETANGENT_SSC_ADDON_ROOT";
        string? original = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-legacy-models.stscene");
        if (!scene.SaveScenario(prior))
            throw new InvalidOperationException("Could not save the observers before importing the legacy models.");
        try
        {
            System.Environment.SetEnvironmentVariable(input, root);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The model catalogue did not load through the ordinary SSC entry point.");
            var session = scene.Session!;
            var sphere = new ObjectId("ssc/sol/neptune/archive-rough-sphere");
            var panel = new ObjectId("ssc/sol/earth/ascii-geometry-witness");
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            var first = View(sphere, epoch, 680);
            var second = View(panel, epoch.AddSeconds(3600), .07);
            string path = Path.Combine(directory, "legacy-cms-ascii.stscene");
            ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                throw new InvalidOperationException("The imported CMS and text CMOD cameras must survive reload.");
            Check(0, sphere, 9800, textured: false);
            Check(1, panel, 1, textured: true);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var picture = GetViewport().GetTexture().GetImage();
            if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the CMS and text CMOD models.");
            GD.Print("Legacy meshes GPU: original 9800-triangle CMS and textured text CMOD in two independent cameras through reload.");

            ObserverView View(ObjectId id, JulianInstant time, double range)
            {
                if (!session.World.Graph.Evaluate(time).TryState(id, out var state))
                    throw new InvalidOperationException($"Missing installed model {id}.");
                var outward = Unit(new(.18, -.38, .91));
                var north = Unit(new Vector3d(0, 1, 0) - outward * outward.Y);
                var camera = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(outward * range, outward * -1, north) };
                return new(time, CameraMode.Solar, id, false) { Navigation = camera };
            }

            void Check(int view, ObjectId id, int triangles, bool textured)
            {
                if (scene.Renderer(view).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh })
                    throw new InvalidOperationException($"No published imported mesh for {id}.");
                int vertices = 0;
                bool hasTexture = false;
                for (int index = 0; index < mesh.GetSurfaceCount(); index++)
                {
                    using var arrays = mesh.SurfaceGetArrays(index);
                    vertices += arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                    hasTexture |= mesh.SurfaceGetMaterial(index) is StandardMaterial3D { AlbedoTexture: not null };
                }
                if (vertices != 3 * triangles || textured != hasTexture)
                    throw new InvalidOperationException($"Unexpected vertices or texture for {id}.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, original);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the original installation after importing the legacy models.");

        static Vector3d Unit(Vector3d vector) => vector / vector.Length;
    }
}
