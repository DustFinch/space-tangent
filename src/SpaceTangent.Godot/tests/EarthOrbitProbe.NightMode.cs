// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseNightMode(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        scene.ShowFullMoon(0);
        string profile = Path.Combine(directory, "archived-night-mode.json");
        File.WriteAllText(profile, """{"DeltaT":"MorrisonStephenson2004","NightMode":true}""");
        await scene.ImportStartupSettings(profile);
        string scenario = Path.Combine(directory, "night-mode.stscene");
        if (!scene.SaveScenario(scenario) || !ScenarioFile.Load(scenario).NightMode || !scene.LoadScenario(scenario) ||
            !await scene.ReloadAddOns() || !scene.Session!.NightMode)
            throw new InvalidOperationException("The archived night mode did not survive scenario open and add-on reload.");

        using var red = await Frame();
        GetWindow().PushInput(new InputEventKey { Keycode = Key.N, AltPressed = true, Pressed = true });
        GetWindow().PushInput(new InputEventKey { Keycode = Key.N, AltPressed = true, Pressed = false });
        if (session.NightMode)
            throw new InvalidOperationException("Alt+N must toggle the red mode.");
        using var normal = await Frame();
        int matched = 0, coloured = 0;
        for (int y = 12; y < normal.GetHeight(); y += 8)
        for (int x = 12; x < normal.GetWidth(); x += 8)
        {
            Color before = normal.GetPixel(x, y);
            Color after = red.GetPixel(x, y);
            if (before.G < .08f && before.B < .08f)
                continue;
            coloured++;
            double luminance = before.R * .2126 + before.G * .7152 + before.B * .0722;
            if (after.G == 0 && after.B == 0 && Math.Abs(after.R - luminance) < .012)
                matched++;
        }
        if (coloured < 150 || matched < coloured * .9)
            throw new InvalidOperationException($"Expected the archived luminance over the scene and workspace. Matched {matched}/{coloured}.");
        if (red.SavePng(Path.Combine(directory, "night-mode-red.png")) != Error.Ok ||
            normal.SavePng(Path.Combine(directory, "night-mode-off.png")) != Error.Ok)
            throw new IOException("Could not save the night-mode comparison.");
        GD.Print($"Night mode GPU: {matched}/{coloured} luminance samples, full Moon, workspace and restored scenario.");

        async Task<Image> Frame()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return GetViewport().GetTexture().GetImage();
        }
    }
}
