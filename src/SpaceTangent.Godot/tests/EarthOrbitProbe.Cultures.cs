// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedSkyGrids(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (DisplayServer.GetName() == "headless")
            return;
        Vector3 solstice = BrightSkyOverlay.EclipticDirection(90, 0);
        Vector3 galacticCenter = BrightSkyOverlay.GalacticDirection(0, 0);
        if (solstice.DistanceTo(BrightSkyData.Equatorial(90, 23.43929f)) > .00001f ||
            galacticCenter.DistanceTo(BrightSkyData.Equatorial(266.4051f, -28.9362f)) > .00002f)
            throw new InvalidOperationException("The grid frames must match the J2000 solstice and galactic centre.");

        var session = scene.Session!;
        var forward = new Vector3d(1, 0, 0);
        var up = new Vector3d(0, 0, 1);
        var offset = new Vector3d(0, 2e8, 0);
        var navigation = new NavigationCamera(session.World.StellarOrigin, offset.Length, 0, 0, null, false)
            { Placement = new NavigationPlacement(offset, forward, up) };
        var clear = new ObserverView(epoch, CameraMode.Solar, null, false)
            { Navigation = navigation, FieldOfViewDegrees = 100, Sky = new(true, false, false, false) };
        var scenario = new SimulationScenario([clear, clear], false, 0, 0, session.DeltaT);
        string file = Path.Combine(directory, "sky-coordinate-grids.stscene");
        string profile = Path.Combine(directory, "sky-coordinate-grids.json");
        ScenarioFile.Save(file, scenario);
        if (!scene.LoadScenario(file))
            throw new InvalidOperationException("The baseline sky grid scenario could not open.");
        using var baseline = await Screenshot();
        int[] masks = string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SPACETANGENT_CONSTELLATION_BOUNDARIES"))
            ? [8, 16, 32] : [8, 16, 32, 4];
        foreach (int mask in masks)
        {
            File.WriteAllText(profile, $$"""{"Site":{},"SkyLayers":{{mask}}}""");
            var imported = LegacyObserverProfiles.Read(profile, clear, session.DeltaT);
            var withGrids = clear with { Sky = imported.Observer.Sky };
            if (mask == 4 && !withGrids.Sky.ConstellationBoundaries)
                throw new InvalidOperationException("The archived boundary flag was not restored.");
            file = Path.Combine(directory, $"sky-grid-{mask}.stscene");
            ScenarioFile.Save(file, scenario with { Views = [withGrids, clear] });
            if (!scene.LoadScenario(file) || session[0] != withGrids || session[1] != clear)
                throw new InvalidOperationException("The archived grid must apply only to its saved observer.");
            using var shown = await Screenshot();
            int changed = 0;
            for (int y = 12; y < shown.GetHeight() - 12; y += 2)
            for (int x = 12; x < shown.GetWidth() - 12; x += 2)
                if (!shown.GetPixel(x, y).IsEqualApprox(baseline.GetPixel(x, y)))
                    changed++;
            if (changed < 30 || shown.SavePng(Path.Combine(directory, $"sky-grid-{mask}.png")) != Error.Ok)
                throw new InvalidOperationException($"Archived sky layer {mask} changed only {changed} rendered samples.");
            GD.Print($"Sky grids GPU: archived layer {mask} changed {changed} samples.");
        }
        var expected = session.CaptureScenario(0);
        if (!await scene.ReloadAddOns() || session[0] != expected.Views[0] || session[1] != clear)
            throw new InvalidOperationException("The archived grid must survive add-on reload.");

        async Task<Image> Screenshot()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return scene.Renderer(0).GetViewport().GetTexture().GetImage();
        }
    }

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
