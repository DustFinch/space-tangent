// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSkyMarkings(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var site = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory").ObserverSite;
        var epoch = TimeConversion.FromUtc(new(new(2024, 3, 10), 4, 30), TimeScale.Tdb);
        var view = new ObserverView(epoch, CameraMode.Surface, null, false)
        {
            EarthObserver = new(site, 0, 89),
            FieldOfViewDegrees = 75
        };
        string profile = Path.Combine(directory, "sky-markings.json");
        File.WriteAllText(profile, """{"Site":{},"SkyMarks":622}""");
        var marked = LegacyObserverProfiles.Read(profile, view, session.DeltaT).Observer;
        const SkyMarkings expected = SkyMarkings.Meridian | SkyMarkings.PrimeVertical | SkyMarkings.GalacticEquator |
            SkyMarkings.PrecessionCircles | SkyMarkings.CircumpolarCircles | SkyMarkings.ZenithAndNadir;
        string path = Path.Combine(directory, "paranal-sky-markings.stscene");
        ScenarioFile.Save(path, new([marked, marked with { Sky = view.Sky }], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session[0].Sky.Markings != expected || session[1].Sky.Markings != SkyMarkings.None)
            throw new InvalidOperationException("Sky markings did not survive reload independently.");

        for (int frame = 0; frame < 5; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var a = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        using var b = scene.Renderer(1).GetViewport().GetTexture().GetImage();
        int vertical = 0, horizontal = 0;
        int cx = Math.Min(a.GetWidth(), b.GetWidth()) / 2;
        int cy = Math.Min(a.GetHeight(), b.GetHeight()) / 2;
        int zenithY = cy - (int)Math.Round(cy * Math.Tan(Math.PI / 180) / Math.Tan(75 * Math.PI / 360));
        for (int offset = -200; offset <= 200; offset++)
        {
            for (int width = -3; width <= 3; width++)
            {
                if (a.GetPixel(cx + width, cy + offset).R > b.GetPixel(cx + width, cy + offset).R + .12f)
                    vertical++;
                if (a.GetPixel(cx + offset, zenithY + width).R > b.GetPixel(cx + offset, zenithY + width).R + .12f)
                    horizontal++;
            }
        }
        if (vertical < 100 || horizontal < 100)
            throw new InvalidOperationException($"Expected both horizon circles through zenith ({vertical}/{horizontal}).");
        using var comparison = GetViewport().GetTexture().GetImage();
        if (comparison.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the sky markings.");
        var menu = ((MenuButton)scene.FindChild("SkyMarkings", recursive: true, owned: false)).GetPopup();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)SkyMarkings.Meridian);
        if (session[0].Sky.Markings != (expected & ~SkyMarkings.Meridian) || session[1].Sky.Markings != SkyMarkings.None)
            throw new InvalidOperationException("The markings menu must affect only the active view.");
        GD.Print($"Sky markings GPU: Paranal, six archived flags and independent views, {vertical}/{horizontal} bright horizon-circle samples after reload.");
    }
}
