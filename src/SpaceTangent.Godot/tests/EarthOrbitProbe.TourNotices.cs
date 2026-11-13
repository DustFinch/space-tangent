// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseTourNotices(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_NOTICE_PROBE") is not ("1" or "only"))
            return;
        string root = System.Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        string script = Path.Combine(directory, "demo-localized.lua");
        File.WriteAllText(script, TourConverter.ConvertFile(Path.Combine(root, "demo_ru.cel")).Lua);

        var session = scene.Session!;
        var left = session[0] with { Playing = false };
        var right = left with { Epoch = left.Epoch.AddSeconds(3600) };
        string scenario = Path.Combine(directory, "tour-caption.stscene");
        ScenarioFile.Save(scenario, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(scenario))
            throw new InvalidOperationException("Could not open two observers for the localized tour.");
        var running = scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the original localized tour.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var renderer = scene.Renderer(0);
        if (renderer.VisibleScriptNotice is not { } text || !text.StartsWith("Знакомство", StringComparison.Ordinal) ||
            scene.Renderer(1).VisibleScriptNotice is not null || session[1] != right ||
            !session[0].Sky.HideClouds || !session[0].Sky.HideOrbits)
            throw new InvalidOperationException("Only the left observer should draw the opening of the localized tour.");
        using var with = renderer.GetViewport().GetTexture().GetImage();
        using var full = GetViewport().GetTexture().GetImage();
        if (full.SavePng(Path.ChangeExtension(scenario, ".png")) != Error.Ok)
            throw new IOException("Could not save the localized tour view.");

        running.Stop();
        session.Tick(6);
        scene.Seek(0, left.Epoch);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var without = renderer.GetViewport().GetTexture().GetImage();
        Vector2I middle = with.GetSize() / 2;
        int glyphSamples = 0;
        for (int y = middle.Y - 22; y < middle.Y + 56; y++)
        for (int x = middle.X; x < with.GetWidth() - 2; x++)
        {
            Color before = with.GetPixel(x, y), after = without.GetPixel(x, y);
            if (before.R + before.G + before.B > 2 && after.R + after.G + after.B < 1)
                glyphSamples++;
        }
        if (renderer.VisibleScriptNotice is not null || glyphSamples < 80)
            throw new InvalidOperationException($"Expected the localized glyphs to expire after visible rendering, got {glyphSamples} samples.");
        GD.Print($"Localized tour: opening text only in the left observer, {glyphSamples} visible glyph samples and independent expiry.");
    }
}
