// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionGlobes(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_GLOBES_PROBE") != "1")
            return;
        var session = scene.Session!;
        var original = new ObjectId("mission/ceres");
        var mapped = new ObjectId("mission/triaxial-shape-witness");
        var epoch = JulianInstant.FromJulianDate(2455755.2, TimeScale.Tdb);
        var left = Observer(original, epoch);
        var right = Observer(mapped, epoch.AddSeconds(120));
        string path = Path.Combine(directory, "mission-globes.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the original and triaxial globes.");
        Verify();
        if (!await scene.ReloadAddOns() || session[0] != left || session[1] != right)
            throw new InvalidOperationException("Both globe views must retain their independent epochs and camera poses.");
        Verify();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the mission globes.");
        GD.Print("Mission globes: original Ceres 500/500/480 km with missing map diagnosed; 500/435/390 km witness with real Ceres JPEG and body rotation verified in the scene graph.");

        ObserverView Observer(ObjectId target, JulianInstant at)
        {
            var snapshot = session.World.Graph.Evaluate(at);
            if (!snapshot.TryState(target, out var body))
                throw new InvalidOperationException($"Missing globe {target.Value}.");
            Vector3d ray = snapshot.SolarSystem!.Value.Sun.PositionKilometers - body.PositionKilometers;
            Vector3d from = (ray + Vector3d.Cross(ray, new(0, 0, 1)) * .54);
            from /= from.Length;
            Vector3d up = new Vector3d(0, 0, 1) - from * from.Z;
            const double range = 2200;
            return new(at, CameraMode.Solar, target, false)
            {
                Navigation = new NavigationCamera(body.PositionKilometers, range, 0, 0, target, true)
                { Placement = new NavigationPlacement(from * range, from * -1, up / up.Length) }
            };
        }

        void Verify()
        {
            foreach (var (index, id, ratioY, ratioZ, hasMap) in new[]
            {
                (0, original, .96, 1d, false), (1, mapped, .78, .87, true)
            })
            {
                if (scene.Renderer(index).ImportedBodyNode(id) is not { Visible: true, Mesh: SphereMesh } node ||
                    Math.Abs(node.Scale.Y / node.Scale.X - ratioY) > 2e-6 ||
                    Math.Abs(node.Scale.Z / node.Scale.X - ratioZ) > 2e-6 ||
                    (node.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null }) != hasMap)
                    throw new InvalidOperationException($"Globe dimensions or surface material unavailable: {id.Value}.");
            }
        }
    }
}
