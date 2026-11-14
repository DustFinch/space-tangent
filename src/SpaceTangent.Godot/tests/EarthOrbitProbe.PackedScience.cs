// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task CapturePackedScience(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_PACKED_SCIENCE") is not ("1" or "only") ||
            DisplayServer.GetName() == "headless")
            return;
        var world = scene.Session!.World;
        int expected = System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE") is { Length: > 0 } ? 3 : 2;
        if (world.Satellites.Count != expected || world.Satellites.Any(track => Path.GetExtension(track.MeshPath) != ".glb"))
            throw new InvalidOperationException("The GPU probe requires the expected set of archived GLB spacecraft.");
        foreach (var track in world.Satellites)
        {
            var snapshot = world.Graph.Evaluate(track.Epoch);
            if (!snapshot.TryState(track.Id, out var state))
                throw new InvalidOperationException($"The packed track is unavailable at epoch for {track.Name}.");
            var camera = new NavigationCamera(state.PositionKilometers, .055, .63, .28, track.Id, true);
            var observer = new ObserverView(track.Epoch, CameraMode.Solar, track.Id, false) { Navigation = camera };
            string name = "packed-" + track.Name.ToLowerInvariant();
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([observer], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not open the packed spacecraft view for {track.Name}.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (scene.Renderer(0).ScienceBodyNode(track.Id) is not { Visible: true, Mesh: ArrayMesh model } ||
                !Enumerable.Range(0, model.GetSurfaceCount()).Any(index =>
                    model.SurfaceGetMaterial(index) is StandardMaterial3D { AlbedoTexture: not null }))
                throw new InvalidOperationException($"The packed textured mesh was not drawn for {track.Name}.");
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                throw new IOException($"Could not capture the packed spacecraft {track.Name}.");
            GD.Print($"Archived textured GLB rendered: {track.Name}, {model.GetSurfaceCount()} surfaces.");
        }
    }
}
