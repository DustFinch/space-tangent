// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExercisePanoramas(SolarSystemScene scene, string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_LANDSCAPES")))
            return;
        var session = scene.Session!;
        var epoch = TimeConversion.FromUtc(new(new(2024, 6, 21), 11), TimeScale.Tdb);
        var camera = new EarthObserverCamera(new(46.2, 6.15, .375), 85, 2.5);
        var without = new ObserverView(epoch, CameraMode.Surface, null, false) { EarthObserver = camera };
        string scenario = Path.Combine(directory, "panoramas.stscene");
        ScenarioFile.Save(scenario, new([without, without], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(scenario))
            throw new InvalidOperationException("The landscape observers could not be opened.");
        foreach (string id in new[] { "geneva", "grossmugl", "garching", "trees" })
        {
            scene.SetLandscapeChoice(0, id);
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (scene.Renderer(0).VisibleLandscape is null)
            {
                if (started.Elapsed.TotalSeconds > 12)
                {
                    string? status = (scene.FindChild("WorkspaceStatus", true, false) as Label)?.Text;
                    throw new InvalidOperationException($"The original landscape {id} did not load. {status}");
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (scene.Renderer(1).VisibleLandscape is not null || scene.Session![1] != without)
                throw new InvalidOperationException("A landscape on the left changed the right observer.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var left = scene.Renderer(0).GetViewport().GetTexture().GetImage();
                using var right = scene.Renderer(1).GetViewport().GetTexture().GetImage();
                using var full = GetViewport().GetTexture().GetImage();
                if (id == "geneva" && (Center(left).G <= Center(left).R + .12f || Center(left).IsEqualApprox(Center(right))))
                    throw new InvalidOperationException($"The Geneva ground should mask the original 2.5° view. {Center(left)} {Center(right)}");
                if (full.SavePng(Path.Combine(directory, $"panorama-{id}.png")) != Error.Ok)
                    throw new IOException($"Could not save the {id} panorama.");
            }
        }
        var expected = scene.Session!.CaptureScenario(0);
        ScenarioFile.Save(scenario, expected);
        scene.SetLandscapeChoice(0, null);
        if (scene.Renderer(0).VisibleLandscape is not null || !scene.LoadScenario(scenario) || !await scene.ReloadAddOns() ||
            scene.Session[0] != expected.Views[0] || scene.Session[1] != without || scene.Renderer(0).VisibleLandscape is null)
            throw new InvalidOperationException("The original landscape must survive scenario round trip and add-on reload.");
        GD.Print("Landscape GPU: Geneva, Grossmugl, Garching and Trees from the original local corpus.");

        static Color Center(Image image)
        {
            Vector2I center = image.GetSize() / 2;
            return image.GetPixel(center.X, center.Y);
        }
    }
}
