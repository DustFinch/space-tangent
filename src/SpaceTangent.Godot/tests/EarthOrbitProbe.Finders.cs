// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseFinders(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_OPTICAL_FINDERS_PROBE") != "1")
            return;
        var session = scene.Session!;
        var left = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
        {
            EarthObserver = EarthObserverCamera.Moscow,
            FieldOfViewDegrees = 6.5,
            Optics = new(ObserverOpticsLibrary.Default, false) { Finders = new(true, CameraSensorPreset.None, true) }
        };
        var right = left with
        {
            FieldOfViewDegrees = 4,
            Optics = new(ObserverOpticsLibrary.Default with { Barlow = 2 }, false)
            { Finders = new(false, CameraSensorPreset.FullFrame, false) }
        };
        string path = Path.Combine(directory, "optical-finders.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != left || session[1] != right ||
            !scene.Renderer(0).FindersVisible || !scene.Renderer(1).FindersVisible)
            throw new InvalidOperationException("Both independent finders must be restored on reload.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var (red, _) = Count(scene.Renderer(0));
        var (_, green) = Count(scene.Renderer(1));
        using (var window = GetViewport().GetTexture().GetImage())
            if (window.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not save the optical finder views.");
        if (red < 150 || green < 150)
            throw new InvalidOperationException($"The real finder geometry generated only {red}/{green} colored samples.");
        ScenarioFile.Save(path, new([left with { Optics = left.Optics! with { Active = true } }, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || scene.Renderer(0).FindersVisible || !scene.Renderer(0).EyepieceVisible ||
            !scene.Renderer(1).FindersVisible)
            throw new InvalidOperationException("Only the active eyepiece must hide its finders.");
        GD.Print($"Optical finders: original Telrad ring angles, full-frame sensor at 2x Barlow, {red}/{green} colored GPU samples, two views restored.");

        static (int Red, int Green) Count(SimulationViewport view)
        {
            int red = 0, green = 0;
            using var image = view.GetViewport().GetTexture().GetImage();
            for (int y = 4; y < image.GetHeight() - 4; y++)
            for (int x = 4; x < image.GetWidth() - 4; x++)
            {
                Color color = image.GetPixel(x, y);
                if (color.R > .58f && color.G < color.R * .55f && color.B < color.R * .52f)
                    red++;
                if (color.G > .6f && color.R < color.G * .66f && color.B < color.G * .82f)
                    green++;
            }
            return (red, green);
        }
    }
}
