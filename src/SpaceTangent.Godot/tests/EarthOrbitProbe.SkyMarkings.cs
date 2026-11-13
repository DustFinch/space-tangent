// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseBodyPath(SolarSystemScene scene, string directory, JulianInstant epoch)
    {
        var session = scene.Session!;
        var site = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory").ObserverSite;
        var solar = session.World.Graph.Evaluate(epoch).SolarSystem!.Value;
        var light = SpaceTangent.Core.Observing.EarthSurfaceLighting.Sample(solar, site, session.DeltaT);
        var moon = SpaceTangent.Core.Ephemerides.SolarSystemSample.MoonId;
        var horizontal = SpaceTangent.Core.Observing.EarthObserving.HorizontalDirection(light, light.EarthToMoon)!.Value;
        var first = new ObserverView(epoch, CameraMode.Surface, moon, false)
        {
            EarthObserver = new(site, horizontal.AzimuthDegrees!.Value, horizontal.AltitudeDegrees),
            FieldOfViewDegrees = 75
        };
        string profile = Path.Combine(directory, "archived-body-path.json");
        File.WriteAllText(profile, """{"Site":{},"SkyMarks":16384}""");
        var marked = first with { Sky = LegacyObserverProfiles.Read(profile, first, session.DeltaT).Observer.Sky };
        string path = Path.Combine(directory, "paranal-moon-path.stscene");
        ScenarioFile.Save(path, new([marked, first], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session[0].Sky.Markings != SkyMarkings.SelectedBodyPath || session[1] != first)
            throw new InvalidOperationException("The archived path flag and the second observer must survive reload.");

        await Frames();
        using var enabled = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        var menu = ((MenuButton)scene.FindChild("SkyMarkings", recursive: true, owned: false)).GetPopup();
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)SkyMarkings.SelectedBodyPath);
        await Frames();
        using var disabled = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        int gold = 0;
        for (int y = 0; y < enabled.GetHeight(); y++)
            for (int x = 0; x < enabled.GetWidth(); x++)
            {
                Color pixel = enabled.GetPixel(x, y);
                if (pixel.R > disabled.GetPixel(x, y).R + .13f && pixel.R > pixel.B + .2f)
                    gold++;
            }
        if (gold < 150 || session[0].Sky.Markings != SkyMarkings.None || session[1] != first)
            throw new InvalidOperationException($"The selected Moon path must affect only the active view ({gold} pixels).");
        if (enabled.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not save the Moon path.");
        GD.Print($"Selected body path GPU: Paranal, archived Moon flag, {gold} gold pixels after reload and independent toggle.");

        async Task Frames()
        {
            for (int frame = 0; frame < 4; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }
    }

    private async Task ExerciseCatalogueMarkings(SolarSystemScene scene, string directory, JulianInstant epoch)
    {
        var pulsars = SkyMarkerCatalogue.Load("SPACETANGENT_PULSARS", "sky/pulsars.txt", true);
        var quasars = SkyMarkerCatalogue.Load("SPACETANGENT_QUASARS", "sky/quasars.txt", false);
        if (pulsars.Length != 3604 || quasars.Length != 6514 || SkyMarkerCatalogue.NavigationalStars.Length != 58)
            throw new InvalidOperationException($"Unexpected original catalogue counts {pulsars.Length}/{quasars.Length}.");
        var marker = pulsars.Single(item => item.Alias == "CTA 1");
        var session = scene.Session!;
        var solar = session.World.Graph.Evaluate(epoch).SolarSystem!.Value;
        var first = new ObserverView(epoch, CameraMode.Solar, null, false)
        {
            Navigation = new(solar.Earth.PositionKilometers, 30000, 0, 0, null, false)
        };
        string path = Path.Combine(directory, "catalogue-sky-markings.stscene");
        ScenarioFile.Save(path, new([first, first], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open catalogue search scene.");
        var field = (LineEdit)scene.FindChild("ObjectQuery", recursive: true, owned: false);
        field.Text = "CTA 1";
        field.EmitSignal(LineEdit.SignalName.TextSubmitted, field.Text);
        if (!session[0].Sky.Markings.HasFlag(SkyMarkings.Pulsars) || session[1] != first)
            throw new InvalidOperationException("Pulsar alias search must mark only the active view.");
        var forward = session[0].Navigation!.Value.Placement!.Value.Forward;
        if ((forward - new Vector3d(marker.Direction.X, marker.Direction.Y, marker.Direction.Z)).Length > 1e-6)
            throw new InvalidOperationException("The active camera must point to the archived pulsar.");
        ScenarioFile.Save(path, session.CaptureScenario(0));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session[0].Sky.Markings != SkyMarkings.Pulsars || session[1] != first)
            throw new InvalidOperationException("Catalogue marks did not survive reload.");

        for (int frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var a = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        int green = 0;
        for (int y = a.GetHeight() / 2 - 18; y <= a.GetHeight() / 2 + 18; y++)
            for (int x = a.GetWidth() / 2 - 18; x <= a.GetWidth() / 2 + 18; x++)
            {
                Color color = a.GetPixel(x, y);
                if (color.G > .35f && color.G > color.R + .1f)
                    green++;
            }
        if (green < 5 || a.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new InvalidOperationException($"Expected the pulsar ring at the centre of the view ({green} green pixels).");
        var quasar = quasars.First(item => item.Magnitude < 14);
        field.Text = quasar.Name;
        field.EmitSignal(LineEdit.SignalName.TextSubmitted, field.Text);
        if (!session[0].Sky.Markings.HasFlag(SkyMarkings.Quasars) || session[1] != first)
            throw new InvalidOperationException("Quasar search must mark only the active view.");
        for (int frame = 0; frame < 3; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var quasarImage = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        var center = quasarImage.GetPixel(quasarImage.GetWidth() / 2, quasarImage.GetHeight() / 2);
        if (!(center.R > center.B + .2f))
            throw new InvalidOperationException($"Expected a quasar at the centre of the view: {center}.");
        GD.Print($"Catalogue marks GPU: {pulsars.Length} pulsars, {quasars.Length} quasars, 58 navigational HIP IDs, CTA 1 and {quasar.Name} search, {green} ring pixels after reload.");
    }

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
        File.WriteAllText(profile, """{"Site":{},"SkyMarks":4095}""");
        var marked = LegacyObserverProfiles.Read(profile, view, session.DeltaT).Observer;
        const SkyMarkings expected = SkyMarkings.Meridian | SkyMarkings.PrimeVertical | SkyMarkings.GalacticEquator |
            SkyMarkings.PrecessionCircles | SkyMarkings.CircumpolarCircles | SkyMarkings.ZenithAndNadir |
            SkyMarkings.EquatorOfDate | SkyMarkings.Colures | SkyMarkings.Poles | SkyMarkings.EquinoxesAndSolstices | SkyMarkings.ArchaeoLines | SkyMarkings.NavigationalStars;
        var j2000 = JulianInstant.FromJulianDate(JulianInstant.J2000Day, TimeScale.Tdb);
        var poleAtEpoch = CelestialPoleOfDate.Direction(j2000)!.Value;
        var poleAfter = CelestialPoleOfDate.Direction(j2000.AddSeconds(36525d * 86400))!.Value;
        var poleBefore = CelestialPoleOfDate.Direction(j2000.AddSeconds(-36525d * 86400))!.Value;
        if ((poleAtEpoch - new Vector3d(0, 0, 1)).Length > 1e-5 || Math.Abs(poleAfter.Length - 1) > 1e-12 ||
            poleAfter.X is < .009 or > .011 || poleBefore.X is < -.011 or > -.009)
            throw new InvalidOperationException($"Incorrect P03 pole: {poleAtEpoch}, {poleBefore}, {poleAfter}.");
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
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_BODY_PATH_PROBE") == "1")
            await ExerciseBodyPath(scene, directory, epoch);
        else
            await ExerciseCatalogueMarkings(scene, directory, epoch);
        GD.Print($"Sky markings GPU: Paranal, twelve archived flags and independent views, {vertical}/{horizontal} bright horizon-circle samples after reload.");
    }
}
