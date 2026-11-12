// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSkyPhotographs(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_PHOTOGRAPHS_PROBE") != "1")
            return;
        string root = System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_PHOTOGRAPHS")!;
        var images = LocalSkyPhotographs.Read(root);
        Vector3d crab = images.Single(image => Path.GetFileName(image.Path) == "m1dumont.png").Center;
        Vector3d orion = images.Single(image => Path.GetFileName(image.Path) == "m42.png").Center;
        var session = scene.Session!;
        var birth = JulianInstant.FromJulianDate(2106215, TimeScale.Tdb);
        var modern = JulianInstant.FromJulianDate(2460310.5, TimeScale.Tdb);
        var layers = new SkyLayerSettings(true, false, false, false) { DeepSky = true };
        var historical = Space(crab, birth.AddSeconds(-3600), 2);
        var after = Space(crab, birth.AddSeconds(3600), 2);
        string history = Path.Combine(directory, "sky-photo-crab.stscene");
        ScenarioFile.Save(history, new([historical, after], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(history) || !await scene.ReloadAddOns() || session[0] != historical || session[1] != after)
            throw new InvalidOperationException("The archived Crab field and independent epochs must survive reload.");
        using var dated = await Screenshot();
        if (scene.Renderer(0).SpaceSurveyCanvas?.PhotographVisible("m1dumont.png") != false ||
            scene.Renderer(1).SpaceSurveyCanvas?.PhotographVisible("m1dumont.png") != true)
            throw new InvalidOperationException("The Crab photograph must appear only after its recorded birth epoch.");
        if (dated.SavePng(Path.ChangeExtension(history, ".png")) != Error.Ok)
            throw new IOException("Could not save the two historical epochs.");

        var site = new EarthSite(25, 0, 1.2);
        var sample = session.World.Ephemeris.Sample(modern);
        var initial = EarthSurfaceLighting.Sample(sample, site, session.DeltaT);
        double ra = Math.Atan2(orion.Y, orion.X);
        site = site with { LongitudeDegrees = Math.IEEERemainder((ra - initial.EarthRotationRadians) * 180 / Math.PI, 360) };
        var lighting = EarthSurfaceLighting.Sample(sample, site, session.DeltaT);
        var horizontal = EarthObserving.HorizontalDirection(lighting, orion);
        if (lighting.SolarAltitudeRadians > -.30 || horizontal is not { AzimuthDegrees: { } azimuth, AltitudeDegrees: >= 30 } point)
            throw new InvalidOperationException("The original Orion field must be above the dark horizon.");
        var ground = new ObserverView(modern, CameraMode.Surface, null, false)
        {
            EarthObserver = new EarthObserverCamera(site, azimuth, point.AltitudeDegrees) { Conditions = new(1) },
            FieldOfViewDegrees = 6.5,
            Sky = layers
        };
        var withSurvey = Space(orion, modern, 6.5) with { Sky = layers with { SurveyId = "dss" } };
        string surface = Path.Combine(directory, "sky-photo-orion.stscene");
        ScenarioFile.Save(surface, new([ground, withSurvey], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(surface))
            throw new InvalidOperationException("Could not open the Orion photograph and DSS views.");
        using var enabled = await Screenshot();
        if (scene.Renderer(0).SurfaceSurveyCanvas?.PhotographVisible("m42.png") != true ||
            scene.Renderer(1).SpaceSurveyCanvas is not { SurveyId: "dss" } survey || survey.PhotographVisible("m42.png"))
            throw new InvalidOperationException("The Orion photograph must appear through the atmosphere and be hidden by DSS.");
        if (enabled.SavePng(Path.ChangeExtension(surface, ".png")) != Error.Ok)
            throw new IOException("Could not save the original Orion photograph through Earth's atmosphere.");
        string plainPath = Path.Combine(directory, "sky-photo-orion-disabled.stscene");
        ScenarioFile.Save(plainPath, new([ground with { Sky = layers with { DeepSky = false } }, withSurvey], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(plainPath))
            throw new InvalidOperationException("Could not open the original Orion sky without photographs.");
        using var plain = await Screenshot();
        int changed = 0;
        for (int y = 150; y < enabled.GetHeight() - 50; y += 4)
        for (int x = 400; x < (enabled.GetWidth() + 350) / 2 - 30; x += 4)
        {
            Color a = enabled.GetPixel(x, y), b = plain.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .03)
                changed++;
        }
        if (changed < 120)
            throw new InvalidOperationException($"The actual Orion photograph changed only {changed} ground samples.");
        GD.Print($"Sky photographs: {images.Count} original images, Crab birth gate across independent epochs, Orion through Earth's atmosphere ({changed} GPU samples), DSS override and reload.");

        ObserverView Space(Vector3d direction, JulianInstant epoch, double fov)
        {
            var up = new Vector3d(0, 0, 1) - direction * direction.Z;
            var pose = new NavigationCamera(session.World.StellarOrigin + new Vector3d(0, 0, 4e8), 1000, 0, 0, null, false)
                { Placement = new NavigationPlacement(default, direction, up / up.Length) };
            return new(epoch, CameraMode.Solar, null, false) { Navigation = pose, Sky = layers, FieldOfViewDegrees = fov };
        }

        async Task<Image> Screenshot()
        {
            for (int i = 0; i < 2; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            return GetViewport().GetTexture().GetImage();
        }
    }
}
