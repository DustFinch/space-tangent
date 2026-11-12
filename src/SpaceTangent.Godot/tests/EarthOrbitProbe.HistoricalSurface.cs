// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseHistoricalSurface(SolarSystemScene scene, string directory)
    {
        if (scene.Session!.World.Ephemeris is not HistoricalSolarEphemeris)
            return;
        var noon = new JulianInstant(2106215, 0, TimeScale.Tdb);
        var night = noon.AddSeconds(43_200);
        var camera = new EarthObserverCamera(new(51.4779, 0, .046), 180, 60);
        var left = new ObserverView(noon, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = camera };
        var right = left with { Epoch = night };
        string path = Path.Combine(directory, "historical-surface-1054.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || scene.Session[0] != left || scene.Session[1] != right)
            throw new InvalidOperationException("The historical ground views did not survive reload.");
        if (scene.FindChild("EphemeridesTab", recursive: true, owned: false) is not Button tab ||
            scene.FindChild("CivilTwilight", recursive: true, owned: false) is not Label twilight)
            throw new InvalidOperationException("The existing twilight display is unavailable.");
        if (!tab.ButtonPressed)
            tab.EmitSignal(Button.SignalName.Pressed);
        if (!twilight.Text.Contains("1054-07-09", StringComparison.Ordinal) ||
            !twilight.Text.Contains("UT1", StringComparison.Ordinal))
            throw new InvalidOperationException("The historical twilight must display a modeled UT1 clock.");
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var day = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            using var dark = scene.Renderer(1).GetViewport().GetTexture().GetImage();
            using var full = GetViewport().GetTexture().GetImage();
            float daylight = SkyBrightness(day), darkness = SkyBrightness(dark);
            if (daylight - darkness < .2f)
                throw new InvalidOperationException($"The historical ground shader did not distinguish day and night: {daylight:F3}, {darkness:F3}.");
            if (full.SavePng(Path.Combine(directory, "historical-surface-1054.png")) != Error.Ok)
                throw new IOException("Could not save the historical ground views.");
        }
        GD.Print("Historical Earth surface: independent day and night in 1054, modeled UT1 twilight, reload preserved.");

        static float SkyBrightness(Image image)
        {
            Vector2I center = image.GetSize() / 2;
            var color = image.GetPixel(center.X, center.Y);
            return (color.R + color.G + color.B) / 3;
        }
    }
}
