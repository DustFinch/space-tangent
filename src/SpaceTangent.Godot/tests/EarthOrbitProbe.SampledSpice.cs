// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSampledSpice(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_SPICE_PROBE") != "1")
            return;
        var session = scene.Session!;
        var epoch = SimulationSession.MissionPreview;
        var craft = new ObjectId("mission/cassini-soi");
        var narrow = new ObjectId("mission/iss-nac");
        var wide = new ObjectId("mission/iss-wac");
        var snapshot = session.World.Graph.Evaluate(epoch);
        if (!snapshot.TryState(craft, out var spacecraft) || !snapshot.TryState(PlanetCatalogue.SaturnId, out var saturn) ||
            !snapshot.TryOrientation(craft, out _))
            throw new InvalidOperationException("The ordinary sampled mission must resolve the original Cassini track and pointing.");
        var relative = spacecraft - saturn;
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CASSINI_STATE_ORACLE") is { } reference)
        {
            var state = JsonSerializer.Deserialize<double[]>(File.ReadAllText(reference))
                ?? throw new InvalidDataException("Missing native Cassini state.");
            if (state.Length != 6 ||
                (relative.PositionKilometers - new Vector3d(state[0], state[1], state[2])).Length > 1e-4 ||
                (relative.VelocityKilometersPerSecond - new Vector3d(state[3], state[4], state[5])).Length > 1e-8)
                throw new InvalidOperationException($"Cassini must follow the native Saturn-relative state: {relative}.");
            GD.Print("Sampled SPICE: Cassini state matches the native Saturn-relative kernel chain.");
        }
        Vector3d radial = relative.PositionKilometers;
        Vector3d side = Vector3d.Cross(radial, new(0, 0, 1));
        string path = Path.Combine(directory, "sampled-spice-cassini.stscene");
        ScenarioFile.Save(path, new([Observer(.03, side), Observer(95000, side)], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("The Cassini spacecraft and frustum views did not open.");
        Check();
        if (!await scene.ReloadAddOns())
            throw new InvalidOperationException("The original CK and camera geometry did not reload.");
        Check();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the sampled SPICE mission.");

        ObserverView Observer(double range, Vector3d direction)
        {
            Vector3d outward = direction / direction.Length;
            var pose = new NavigationCamera(spacecraft.PositionKilometers, range, 0, 0, craft, false)
                { Placement = new NavigationPlacement(outward * range, outward * -1, new(0, 0, 1)) };
            return new(epoch, CameraMode.Solar, craft, false) { Navigation = pose };
        }

        void Check()
        {
            var close = scene.Renderer(0);
            var distant = scene.Renderer(1);
            if (close.ImportedBodyNode(craft) is not { Visible: true, Mesh: ArrayMesh mesh } || mesh.GetSurfaceCount() < 2)
                throw new InvalidOperationException("The original Cassini 3DS mesh is unavailable.");
            foreach (ObjectId id in new[] { narrow, wide })
            {
                if (distant.MissionSensorResult(id) is not { GridLines: 340 } sensor ||
                    sensor.SurfaceRays + sensor.RangeClips + sensor.Misses != 48)
                    throw new InvalidOperationException("The original CK/IK camera field was not drawn.");
            }
            GD.Print($"Sampled SPICE: original Cassini mesh with {mesh.GetSurfaceCount()} surfaces and both ISS camera frusta.");
        }
    }
}
