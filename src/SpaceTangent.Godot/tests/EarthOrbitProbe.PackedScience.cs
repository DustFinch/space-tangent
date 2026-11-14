// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static async Task ExerciseArchivedOrbitHistory(SolarSystemScene scene, ScienceSatellite track, string directory)
    {
        var session = scene.Session!;
        var saved = Path.Combine(directory, "deep-space-history.stscene");
        var read = new ObserverView(track.Epoch, CameraMode.Solar, track.Id, false);
        var scenario = new SimulationScenario([read, read], false, 0, 0, session.DeltaT);
        Show(scenario);
        var initial = State(1);
        foreach (double minutes in new[] { 1, 29.9, 30, 30.1, 31, 1440, 1439, -5, 0 })
            Show(scenario with { Views = [read with { Epoch = track.Epoch.AddSeconds(minutes * 60) }, read] });
        var advanced = State(0);
        double displacement = (advanced.PositionKilometers - initial.PositionKilometers).Length;
        if (displacement is < .0045 or > .0047)
            throw new InvalidOperationException($"Expected the archived DeepProbe history offset, measured {displacement:R} km.");
        if (!await scene.ReloadAddOns() || State(0) != advanced || State(1) != initial ||
            session[0] != read || session[1] != read || session.DeltaT != scenario.DeltaT)
            throw new InvalidOperationException("Reloading the original archive changed a view, its orbital history or ΔT.");

        GD.Print(FormattableString.Invariant($"Archived SDP4 history: {displacement * 1000:F3} m at the same epoch, retained across reload in independent views."));

        void Show(SimulationScenario value)
        {
            ScenarioFile.Save(saved, value);
            if (!scene.LoadScenario(saved))
                throw new InvalidOperationException("Could not open the archived orbit history scenario.");
        }

        CartesianState State(int view) => scene.ViewSnapshot(view)?.TryState(track.Id, out var state) == true
            ? state : throw new InvalidOperationException("The archived DeepProbe state is missing.");
    }


    private async Task CapturePackedScience(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_PACKED_SCIENCE") is not ("1" or "only") ||
            DisplayServer.GetName() == "headless")
            return;
        var world = scene.Session!.World;
        int expected = System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE") is { Length: > 0 } ? 3 : 2;
        if (world.Satellites.Count != expected || world.Satellites.Any(track => Path.GetExtension(track.MeshPath) != ".glb"))
            throw new InvalidOperationException("The GPU probe requires the expected set of archived GLB spacecraft.");
        if (expected == 3)
            await ExerciseArchivedOrbitHistory(scene, world.Satellites.Single(track => track.Id.Value == "norad/24208"), directory);
        foreach (var track in world.Satellites)
        {
            var epochs = track.Id.Value == "norad/24208"
                ? new[] { (track.Epoch, ""), (track.Epoch.AddSeconds(-315360000), "-before"), (track.Epoch.AddSeconds(315360000), "-after") }
                : [(track.Epoch, "")];
            foreach (var (epoch, suffix) in epochs)
                await Capture(track, epoch, suffix);
        }

        async Task Capture(SpaceTangent.Core.Addons.ScienceSatellite track, SpaceTangent.Contracts.JulianInstant epoch, string suffix)
        {
            var snapshot = world.Graph.Evaluate(epoch);
            if (!snapshot.TryState(track.Id, out var state))
                throw new InvalidOperationException($"The packed track is unavailable at {epoch} for {track.Name}.");
            var camera = new NavigationCamera(state.PositionKilometers, .055, .63, .28, track.Id, true);
            var observer = new ObserverView(epoch, CameraMode.Solar, track.Id, false) { Navigation = camera };
            string name = "packed-" + track.Name.ToLowerInvariant() + suffix;
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
            GD.Print($"Archived textured GLB rendered: {track.Name}{suffix}, {model.GetSurfaceCount()} surfaces.");
        }
    }
}
