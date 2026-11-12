// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseNativeMissionAttitude(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_NATIVE_ATTITUDE_GPU") != "1")
            return;
        var session = scene.Session!;
        string? installation = System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_MISSIONS_ROOT");
        bool packed = installation is not null && File.Exists(Path.Combine(installation, "pack.json"));
        if (packed && (session.World.Catalogue.Find(new("mission/compatiblerig")) is null ||
            session.World.Catalogue.Find(new("mission/invalidrig")) is not null ||
            session.World.Catalogue.Find(new("mission/invalidchild")) is not null))
            throw new InvalidOperationException("The original pack must retain the valid compatibility rig and omit the damaged attitude and its child.");
        var target = new ObjectId("mission/cubesat");
        var center = new ObjectId("mission/ceres");
        var first = JulianInstant.FromJulianDate(2455755, TimeScale.Tdb);
        var middle = JulianInstant.FromJulianDate(2455756, TimeScale.Tdb);
        var outward = Unit(new Vector3d(.46, -.68, .56));
        var up = Unit(new Vector3d(0, 0, 1) - outward * outward.Z);
        const double range = 4.5;
        var left = Observer(first);
        var right = Observer(middle);
        string path = Path.Combine(directory, packed ? "packed-q-orientation.stscene" : "sampled-q-orientation.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the original mission model in the two Q sample epochs.");
        Verify(0, first, interpolate: false);
        Verify(1, middle, interpolate: true);
        if (!await scene.ReloadAddOns() || session[0] != left || session[1] != right)
            throw new InvalidOperationException("Reload must preserve the two sampled attitude cameras.");
        Verify(0, first, interpolate: false);
        Verify(1, middle, interpolate: true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var picture = GetViewport().GetTexture().GetImage();
        if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the original mission model and native attitude.");
        GD.Print($"Sampled Q GPU: original Jason {(packed ? "packed GLB" : "source OBJ")}, checked textured surfaces and both CubeSat attitudes before and after reload.");

        ObserverView Observer(JulianInstant epoch)
        {
            if (!session.World.Graph.Evaluate(epoch).TryState(target, out var position))
                throw new InvalidOperationException("The original mission catalogue did not install CubeSat.");
            var pose = new NavigationCamera(position.PositionKilometers, range, 0, 0, target, true)
            {
                Placement = new NavigationPlacement(outward * range, outward * -1, up)
            };
            return new(epoch, CameraMode.Solar, target, false) { Navigation = pose };
        }

        void Verify(int index, JulianInstant epoch, bool interpolate)
        {
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(center, out var origin) || !snapshot.TryState(target, out var objectState) ||
                !snapshot.TryOrientation(target, out var frame))
                throw new InvalidOperationException("The original mission frames must resolve at both epochs.");
            var relative = origin - objectState;
            Vector3d x = Unit(relative.PositionKilometers);
            Vector3d secondary = relative.VelocityKilometersPerSecond;
            Vector3d y = Unit(x * Vector3d.Dot(x, secondary) - secondary);
            Vector3d z = Vector3d.Cross(x, y);
            var expected = interpolate ? new Basis3d(x, y, z) : new Basis3d(x, z, y * -1);
            if ((frame.ToWorld.X - expected.X).Length > 2e-10 || (frame.ToWorld.Y - expected.Y).Length > 2e-10 ||
                (frame.ToWorld.Z - expected.Z).Length > 2e-10)
                throw new InvalidOperationException($"Source attitude at {epoch} does not compose with its original two-vector frame.");
            if (scene.Renderer(index).ImportedBodyNode(target) is not { Visible: true, Mesh: ArrayMesh mesh } model ||
                mesh.GetSurfaceCount() < 2 || !Enumerable.Range(0, mesh.GetSurfaceCount()).Any(surface =>
                    mesh.SurfaceGetMaterial(surface) is StandardMaterial3D { AlbedoTexture: not null }))
                throw new InvalidOperationException("Expected the original textured Jason model on the archived mission object.");
            var basis = model.Transform.Basis;
            if (!Matches(basis.X, expected.X) || !Matches(basis.Y, expected.Y) || !Matches(basis.Z, expected.Z))
                throw new InvalidOperationException("The rendered mesh columns do not match the source orientation.");
        }

        static Vector3d Unit(Vector3d value) => value / value.Length;
        static bool Matches(Vector3 column, Vector3d expected) =>
            column.Normalized().DistanceTo(new((float)expected.X, (float)expected.Z, (float)-expected.Y)) < 3e-5f;
    }
}
