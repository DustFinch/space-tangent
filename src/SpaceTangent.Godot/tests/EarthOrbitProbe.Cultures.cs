// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSkyCultures(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_CULTURE_GPU") != "1")
            return;
        var session = scene.Session!;
        var west = View(83.82, 2, "western", SkyCultureNames.International, true);
        var chinese = View(182, 56, "chinese", SkyCultureNames.Russian, false);
        var selected = new SimulationScenario([west, chinese], false, 0, 0, session.DeltaT);
        string path = Path.Combine(directory, "sky-cultures.stscene");
        ScenarioFile.Save(path, selected);
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != west || session[1] != chinese)
            throw new InvalidOperationException("The two culture settings and camera poses did not survive reload.");

        using var present = await Screenshot();
        if (scene.Renderer(0).SpaceCultureOverlay is not { RenderedCulture: "western", RenderedArtwork: > 0 } westSky ||
            !westSky.RenderedCultureNames.Contains("Orion") ||
            scene.Renderer(1).SpaceCultureOverlay is not { RenderedCulture: "chinese", RenderedArtwork: 0 } eastSky ||
            !eastSky.RenderedCultureNames.Contains("Северный ковш"))
            throw new InvalidOperationException("The original constellation artwork or independent names were not drawn.");
        if (present.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not save the two cultures.");
        int artwork = westSky.RenderedArtwork;
        var withoutArt = west with { Sky = west.Sky with { ConstellationArt = false } };
        string absent = Path.Combine(directory, "sky-cultures-without-art.stscene");
        ScenarioFile.Save(absent, selected with { Views = [withoutArt, chinese] });
        if (!scene.LoadScenario(absent))
            throw new InvalidOperationException("The sky scene without artwork could not open.");
        using var plain = await Screenshot();
        int changed = 0;
        for (int y = 115; y < present.GetHeight() - 40; y += 3)
        for (int x = 410; x < (present.GetWidth() + 350) / 2 - 8; x += 3)
        {
            Color a = present.GetPixel(x, y), b = plain.GetPixel(x, y);
            if (Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) > .02)
                changed++;
        }
        if (changed < 100)
            throw new InvalidOperationException($"The artwork changed only {changed} GPU samples.");
        GD.Print($"Sky cultures: {artwork} Western drawings and {changed} GPU samples, Orion and Russian Northern Dipper, separate views survived reload.");

        ObserverView View(double ra, double dec, string culture, SkyCultureNames names, bool art)
        {
            Vector3 d = BrightSkyData.Equatorial((float)ra, (float)dec);
            var forward = new Vector3d(d.X, d.Y, d.Z);
            forward /= forward.Length;
            var up = new Vector3d(0, 0, 1) - forward * forward.Z;
            up /= up.Length;
            Vector3d tangent = Vector3d.Cross(forward, up);
            var pose = new NavigationCamera(session.World.StellarOrigin + tangent * 2e8, 5e6, 0, 0, null, false)
                { Placement = new NavigationPlacement(tangent * 5e6, forward, up) };
            return new(epoch, CameraMode.Solar, null, false)
            {
                Navigation = pose,
                FieldOfViewDegrees = 70,
                Sky = new(true, true, false, true) { CultureId = culture, CultureNames = names, ConstellationArt = art }
            };
        }

        async Task<Image> Screenshot()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return GetViewport().GetTexture().GetImage();
        }
    }
}
