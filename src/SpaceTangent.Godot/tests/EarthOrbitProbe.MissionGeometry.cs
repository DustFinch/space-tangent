// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionGeometry(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_GEOMETRY_PROBE") != "1")
            return;
        var session = scene.Session!;
        var id = new ObjectId("mission/timeswitchmodel");
        var model = scene.Renderer(0).ImportedBodyNode(id);
        if (session.World.Catalogue.Find(id) is null || model is null)
            throw new InvalidOperationException("The original timed mesh fixture is unavailable.");
        await Check("before", 2455754.99, null);
        ArrayMesh? first = await Check("first", 2455755.005, 28);
        ArrayMesh? nested = await Check("nested", 2455755.015, 20);
        if (ReferenceEquals(first, nested))
            throw new InvalidOperationException("The nested phase must select the original SORCE mesh.");
        await Check("gap", 2455755.025, null);
        ArrayMesh? recovered = await Check("recovered", 2455755.035, 28);
        if (!ReferenceEquals(first, recovered) || !await scene.ReloadAddOns() ||
            !scene.Renderer(0).ImportedBodyNode(id)!.Visible)
            throw new InvalidOperationException("Restored geometry or its epoch was lost on add-on reload.");
        await Check("ended", 2455755.045, null);
        GD.Print("Original timed geometry rendered: outer and nested meshes, explicit gap, recovery, and end.");

        async Task<ArrayMesh?> Check(string stage, double day, int? surfaceCount)
        {
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            var frame = session.World.Graph.Evaluate(epoch);
            if (!frame.TryState(id, out var state))
                throw new InvalidOperationException("The mission state must remain available even during a geometry gap.");
            var pose = new NavigationCamera(state.PositionKilometers, .055, .48, .14, id, true);
            var observer = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose };
            string path = Path.Combine(directory, "mission-geometry-" + stage);
            ScenarioFile.Save(path + ".stscene", new([observer], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path + ".stscene"))
                throw new InvalidOperationException($"Could not load the timed geometry view {stage}.");
            model = scene.Renderer(0).ImportedBodyNode(id);
            var active = model is { Visible: true, Mesh: ArrayMesh mesh } ? mesh : null;
            if (active?.GetSurfaceCount() != surfaceCount || surfaceCount is null && model?.Visible == true)
                throw new InvalidOperationException($"Unexpected timed geometry at {stage}: {active?.GetSurfaceCount()} surfaces.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(path + ".png") != Error.Ok)
                    throw new IOException($"Could not save timed geometry view {stage}.");
            }
            return active;
        }
    }
}
