// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionAxes(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_AXES_GPU") != "1")
            return;
        var session = scene.Session!;
        var ceres = new ObjectId("mission/ceres");
        var cube = new ObjectId("mission/cubesat");
        var bodyAxes = new ObjectId("mission/body-axes");
        var frameAxes = new ObjectId("mission/frame-axes");
        var epoch = JulianInstant.FromJulianDate(2455755, TimeScale.Tdb);
        var first = View(ceres, epoch, 2900);
        var second = View(cube, epoch.AddSeconds(3600), 7);
        string? installation = System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_MISSIONS_ROOT");
        bool packed = installation is not null && File.Exists(Path.Combine(installation, "pack.json"));
        string path = Path.Combine(directory, packed ? "mission-axes-packed.stscene" : "mission-axes-source.stscene");
        ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open both mission visualizer cameras.");
        Verify();
        if (!await scene.ReloadAddOns() || session[0] != first || session[1] != second)
            throw new InvalidOperationException("Mission axes cameras or independent epochs changed at reload.");
        Verify();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var picture = GetViewport().GetTexture().GetImage();
        if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the mission axes.");
        GD.Print($"Mission axes GPU: {(packed ? "archived pack" : "original catalogue")}, Ceres body axes and CubeSat frame axes with three surfaces, physical scales and reload.");

        ObserverView View(ObjectId target, JulianInstant when, double distance)
        {
            if (!session.World.Graph.Evaluate(when).TryState(target, out var state))
                throw new InvalidOperationException("The authored mission object is missing.");
            var outward = Unit(new(.55, -.78, .29));
            var north = Unit(new Vector3d(0, 0, 1) - outward * outward.Z);
            var camera = new NavigationCamera(state.PositionKilometers, distance, 0, 0, target, true)
                { Placement = new NavigationPlacement(outward * distance, outward * -1, north) };
            return new(when, CameraMode.Solar, target, false) { Navigation = camera };
        }

        void Verify()
        {
            var left = session.World.Graph.Evaluate(first.Epoch);
            var right = session.World.Graph.Evaluate(second.Epoch);
            if (!left.TryOrientation(bodyAxes, out var body) ||
                !right.TryMissionReferenceFrame(cube, orbit: false, second.Epoch, out _, out var frame))
                throw new InvalidOperationException("The imported frames should resolve independently.");
            Check(0, bodyAxes, body.ToWorld, 900 * 48f / 2900, 1);
            Check(1, frameAxes, frame.ToWorld, 2 * 48f / 7, .3f);
        }

        void Check(int index, ObjectId id, Basis3d expected, float scale, float opacity)
        {
            if (scene.Renderer(index).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } node || mesh.GetSurfaceCount() != 3)
                throw new InvalidOperationException("The mission renderer did not publish the three original arrow surfaces.");
            for (int axis = 0; axis < 3; axis++)
            {
                using var arrays = mesh.SurfaceGetArrays(axis);
                if (arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length != 65 ||
                    arrays[(int)Mesh.ArrayType.Index].AsInt32Array().Length != 360 ||
                    mesh.SurfaceGetMaterial(axis) is not StandardMaterial3D style ||
                    style.AlbedoColor != new Color(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, axis == 2 ? 1 : 0, opacity))
                    throw new InvalidOperationException("The visible axes did not retain their original profile and colors.");
            }
            var basis = node.Transform.Basis;
            if (!Matches(basis.X, expected.X) || !Matches(basis.Y, expected.Z) || !Matches(basis.Z, expected.Y * -1))
                throw new InvalidOperationException("The rendered arrows did not receive their body or catalogue frame.");

            bool Matches(Vector3 value, Vector3d direction) => value.DistanceTo(new Vector3((float)direction.X,
                (float)direction.Z, (float)-direction.Y) * scale) < .001f;
        }

        static Vector3d Unit(Vector3d vector) => vector / vector.Length;
    }
}
