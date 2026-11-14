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
        // The original CK points both instrument fields at Saturn at this epoch.
        var second = Observer(141915780, 140_000);
        var model = session.DeltaT;
        string path = Path.Combine(directory, "packed-cassini");
        ScenarioFile.Save(path + ".stscene", new([first, second], false, 0, 0, model));
        if (!scene.LoadScenario(path + ".stscene"))
            throw new InvalidOperationException("Could not open the packed Cassini scene.");
        Check();
        if (!await scene.ReloadAddOns() || session[0] != first || session[1] != second || session.DeltaT != model)
            throw new InvalidOperationException("Packed Cassini reload lost a view, epoch or Delta T.");
        Check();
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TEST_PACKED_CASSINI_ROLLBACK") == "1")
        {
            string root = System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_MISSIONS_ROOT")!;
            string frames = Path.Combine(root, "99-frames");
            string moved = Path.Combine(directory, "frames-" + Guid.NewGuid().ToString("N"));
            var published = session.World;
            Directory.Move(frames, moved);
            try
            {
                bool reloaded = await scene.ReloadAddOns();
                if (reloaded || !ReferenceEquals(published, session.World) ||
                    session[0] != first || session[1] != second || session.DeltaT != model)
                    throw new InvalidOperationException($"Removing the frame-only pack: reloaded {reloaded}, " +
                        $"same world {ReferenceEquals(published, session.World)}, views {session[0] == first}/{session[1] == second}, " +
                        $"same Delta T {session.DeltaT == model}.");
            }
            finally
            {
                Directory.Move(moved, frames);
            }
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("Could not restore the frame-only Cassini pack.");
            Check();
            GD.Print("Packed Cassini: removing the companion directory triggered rollback and restoring it recovered the catalogue.");
        }

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(path + ".png") != Error.Ok)
            throw new IOException("Could not capture the packed Cassini scene.");

        var distant = Observer(141917558.4203589, 60);
        var snapshot = session.World.Graph.Evaluate(distant.Epoch);
        if (!snapshot.TryState(cassini, out var craft) || snapshot.SolarSystem is not { } solar)
            throw new InvalidOperationException("Original Cassini and Sun states are required for photometry.");
        var towardSun = (solar.Sun - craft).PositionKilometers;
        var eyeOffset = distant.Navigation!.Value.Placement!.Value.OffsetKilometers;
        double phase = (1 + Vector3d.Dot(towardSun / towardSun.Length, eyeOffset / eyeOffset.Length)) / 2;
        // The archive's 6 m size scales half the longest mesh axis. Photometry uses the
        // enclosing radius about the model origin, which can be larger for an asymmetric mesh.
        const double authoredHalfExtent = .006;
        var mesh = (ArrayMesh)scene.Renderer(0).ImportedBodyNode(cassini)!.Mesh!;
        var span = mesh.GetAabb().Size;
        double extent = Math.Max(span.X, Math.Max(span.Y, span.Z));
        double originRadius = 0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            using var arrays = mesh.SurfaceGetArrays(surface);
            foreach (Vector3 vertex in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array())
                originRadius = Math.Max(originRadius, vertex.Length());
        }
        double radius = originRadius * (2 * authoredHalfExtent / extent);
        double ratio = radius / eyeOffset.Length * (149_597_870.7 / towardSun.Length);
        double expectedMagnitude = -26.74 - 2.5 * Math.Log10(.5 * phase * ratio * ratio);
        string photometricScene = Path.Combine(directory, "cassini-photometry.stscene");
        ScenarioFile.Save(photometricScene, new([first, distant], false, 0, 0, model));
        if (!scene.LoadScenario(photometricScene))
            throw new InvalidOperationException("Could not open the Cassini photometry scene.");
        var farView = scene.Renderer(1);
        var nearView = scene.Renderer(0);
        var point = farView.BodyPoint(cassini);
        bool nearModel = nearView.ImportedBodyNode(cassini)?.Visible == true;
        bool nearPoint = nearView.BodyPoint(cassini)?.Visible == true;
        bool farModel = farView.ImportedBodyNode(cassini)?.Visible == true;
        bool farPoint = point?.Visible == true;
        double magnitude = point?.MaterialOverride is ShaderMaterial material
            ? material.GetShaderParameter("magnitude").AsSingle() : double.NaN;
        ObjectId? picked = point is not null
            ? farView.PickObject(farView.NavigationCameraNode.UnprojectPosition(point.Position)) : null;
        if (!nearModel || nearPoint || farModel || !farPoint ||
            !(Math.Abs(magnitude - expectedMagnitude) <= 1e-4) || picked != cassini)
            throw new InvalidOperationException($"Cassini photometry: near model {nearModel}, near point {nearPoint}, " +
                $"far model {farModel}, far point {farPoint}, magnitude {magnitude:F5} expected {expectedMagnitude:F5}, pick {picked}.");
        GD.Print($"Mission photometry GPU: packed Cassini point at magnitude {expectedMagnitude:F5} and independent near GLB.");

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
            if (nac is not { GridLines: 340, SurfaceRays: 48, FootprintLines: 48 } || wac is not { GridLines: 340, SurfaceRays: 45, FootprintLines: 44 } ||
                view.GetNode<MeshInstance3D>("MissionSensorFaces").Mesh.GetSurfaceCount() != 1 ||
                view.GetNode<MeshInstance3D>("MissionSensorFootprints").Mesh.GetSurfaceCount() != 1)
                throw new InvalidOperationException($"The packed instrument fields were not drawn: NAC {nac}, WAC {wac}.");
            GD.Print($"Packed Cassini GPU: {geometry.GetSurfaceCount()} GLB surfaces, NAC {nac}, WAC {wac}.");
        }
    }
}
