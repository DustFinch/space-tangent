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

    private async Task ExerciseGltfShuttle(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_GLTF_SHUTTLE_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_GLTF_SHUTTLE") is not { Length: > 0 } root)
            return;

        const string source = "SPACETANGENT_SSC_ADDON_ROOT";
        string? previous = System.Environment.GetEnvironmentVariable(source);
        string original = Path.Combine(directory, "before-gltf-shuttle.stscene");
        if (!scene.SaveScenario(original))
            throw new IOException("Could not save the scenario before the shuttle import.");
        try
        {
            System.Environment.SetEnvironmentVariable(source, root);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The shuttle did not import.");
            var session = scene.Session!;
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            var glb = new ObjectId("ssc/sol/earth/archived-shuttle-glb");
            var gltf = new ObjectId("ssc/sol/earth/archived-shuttle-gltf");
            var first = Observer(glb, epoch);
            var second = Observer(gltf, epoch.AddSeconds(120));
            string path = Path.Combine(directory, "original-and-text-shuttle.stscene");
            ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                throw new InvalidOperationException("The two shuttle observers must survive reload.");
            var originalGeometry = Mesh(0, glb);
            var textGeometry = Mesh(1, gltf);
            if (originalGeometry != textGeometry || originalGeometry is not (58_134, 42, 11))
                throw new InvalidOperationException($"Shuttle geometry or materials differ: {originalGeometry}, {textGeometry}.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the two shuttle models.");
            GD.Print($"glTF shuttle: {textGeometry.Vertices / 3} triangles, {textGeometry.Surfaces} surfaces and {textGeometry.Textures} JPEG textures in original GLB and external glTF; both restored after reload.");

            ObserverView Observer(ObjectId id, JulianInstant at)
            {
                if (!session.World.Graph.Evaluate(at).TryState(id, out var state))
                    throw new InvalidOperationException($"No trajectory for {id.Value}.");
                const double range = .13;
                var outward = new Vector3d(.33, -.45, .83);
                outward /= outward.Length;
                var up = new Vector3d(0, 0, 1) - outward * outward.Z;
                return new(at, CameraMode.Solar, id, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                        { Placement = new NavigationPlacement(outward * range, outward * -1, up / up.Length) }
                };
            }

            (int Vertices, int Surfaces, int Textures) Mesh(int view, ObjectId id)
            {
                if (scene.Renderer(view).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh })
                    throw new InvalidOperationException($"No mesh for {id.Value}.");
                int vertices = 0;
                var textures = new HashSet<Texture2D>();
                for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                {
                    using var arrays = mesh.SurfaceGetArrays(i);
                    vertices += arrays[(int)global::Godot.Mesh.ArrayType.Vertex].AsVector3Array().Length;
                    if (mesh.SurfaceGetMaterial(i) is StandardMaterial3D { AlbedoTexture: { } texture })
                        textures.Add(texture);
                }
                return (vertices, mesh.GetSurfaceCount(), textures.Count);
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(source, previous);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(original))
            throw new InvalidOperationException("Could not restore the original installation after glTF import.");
    }
}
