// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseOptics(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var left = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
        {
            EarthObserver = EarthObserverCamera.Moscow,
            Optics = new(ObserverOpticsLibrary.Default with { SeeingArcseconds = 3 }, true)
        };
        var right = left with { Optics = null };
        string path = Path.Combine(directory, "eyepiece-moon.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !scene.Renderer(0).EyepieceVisible || scene.Renderer(1).EyepieceVisible ||
            !await scene.ReloadAddOns() || scene.Session![0] != left || scene.Session[1] != right)
            throw new InvalidOperationException("The Moon eyepiece must be active only in the saved left view.");
        if (DisplayServer.GetName() != "headless")
        {
            scene.Renderer(0).SetSurfaceControls(clouds: false, lunarLight: false);
            scene.Renderer(1).SetSurfaceControls(clouds: false, lunarLight: false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var eyepiece = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            using var regular = scene.Renderer(1).GetViewport().GetTexture().GetImage();
            using var full = GetViewport().GetTexture().GetImage();
            var center = eyepiece.GetSize() / 2;
            if (eyepiece.GetPixel(4, 4).R > .002f || eyepiece.GetPixel(center.X, center.Y).R < .25f ||
                regular.GetPixel(4, 4).B < .004f)
                throw new InvalidOperationException($"The field stop should mask only the eyepiece and leave the tracked Moon visible. {eyepiece.GetPixel(4, 4)} {eyepiece.GetPixel(center.X, center.Y)} {regular.GetPixel(4, 4)}");
            if (full.SavePng(Path.Combine(directory, "eyepiece-moon.png")) != Error.Ok)
                throw new IOException("Could not save the Moon through its eyepiece.");
            scene.Renderer(0).SetSurfaceControls(clouds: true, lunarLight: true);
            scene.Renderer(1).SetSurfaceControls(clouds: true, lunarLight: true);
        }
        GD.Print("Eyepiece GPU: 48x full Moon and field stop, independent normal view and saved optics after reload.");
    }
}
