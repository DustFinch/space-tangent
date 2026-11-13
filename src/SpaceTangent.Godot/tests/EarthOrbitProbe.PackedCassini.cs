// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExercisePackedCassini(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        ObjectId cassini = new("mission/cassini-soi"), narrow = new("mission/iss-nac"), wide = new("mission/iss-wac");
        var first = Observer(141917558.4203589, .025);
        var second = Observer(141918830.41202343, 140_000);
        var model = session.DeltaT;
        string path = Path.Combine(directory, "packed-cassini");
        ScenarioFile.Save(path + ".stscene", new([first, second], false, 0, 0, model));
        if (!scene.LoadScenario(path + ".stscene"))
            throw new InvalidOperationException("Could not open the packed Cassini scene.");
        Check();
        if (!await scene.ReloadAddOns() || session[0] != first || session[1] != second || session.DeltaT != model)
            throw new InvalidOperationException("Packed Cassini reload lost a view, epoch or Delta T.");
        Check();

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(path + ".png") != Error.Ok)
            throw new IOException("Could not capture the packed Cassini scene.");

        ObserverView Observer(double seconds, double range)
        {
            var epoch = JulianInstant.FromTdbSeconds(seconds);
            if (!session.World.Graph.Evaluate(epoch).TryState(cassini, out var state))
                throw new InvalidOperationException("Could not evaluate the packed Cassini trajectory.");
            var eye = new Vector3d(.5, -.6, .7);
            eye /= eye.Length;
            var up = new Vector3d(0, 0, 1) - eye * eye.Z;
            var pose = new NavigationCamera(state.PositionKilometers, range, 0, 0, cassini, true)
                { Placement = new NavigationPlacement(eye * range, eye * -1, up / up.Length) };
            return new(epoch, CameraMode.Solar, cassini, false) { Navigation = pose };
        }

        void Check()
        {
            if (scene.Renderer(0).ImportedBodyNode(cassini) is not { Visible: true, Mesh: ArrayMesh geometry } ||
                geometry.GetSurfaceCount() < 2)
                throw new InvalidOperationException("The packed Cassini GLB was not published.");
            var view = scene.Renderer(1);
            var nac = view.MissionSensorResult(narrow);
            var wac = view.MissionSensorResult(wide);
            if (nac is not { GridLines: 340 } || wac is not { GridLines: 340 } ||
                view.GetNode<MeshInstance3D>("MissionSensorFaces").Mesh.GetSurfaceCount() != 1)
                throw new InvalidOperationException($"The packed instrument fields were not drawn: NAC {nac}, WAC {wac}.");
            GD.Print($"Packed Cassini GPU: {geometry.GetSurfaceCount()} GLB surfaces, NAC {nac}, WAC {wac}.");
        }
    }
}
