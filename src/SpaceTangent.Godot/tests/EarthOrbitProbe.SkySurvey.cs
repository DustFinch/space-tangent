// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseLocalSkySurvey(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_SURVEY_PROBE") != "1")
            return;
        var session = scene.Session!;
        var epoch = JulianInstant.FromJulianDate(2460310.5, TimeScale.Tdb);
        var sky = new SkyLayerSettings(true, false, false, false) { SurveyId = "dss", SurveyBrightness = .8 };
        Vector3d orion = Direction(83.82f, -5.39f);
        Vector3d pleiades = Direction(56.75f, 24.12f);
        var left = Space(orion);
        var right = Space(pleiades);
        string path = Path.Combine(directory, "sky-survey-detail-and-overview.stscene");
        ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != left || session[1] != right)
            throw new InvalidOperationException("The local sky survey and both cameras did not survive reload.");
        using var orbital = await Screenshot();
        if (scene.Renderer(0).SpaceSurveyCanvas is not { SurveyId: "dss", DetailPatches: > 0, FinestSourceOrder: 5 } detail ||
            scene.Renderer(1).SpaceSurveyCanvas is not { SurveyId: "dss", DetailPatches: 0, OverviewPatches: > 0 } overview)
            throw new InvalidOperationException("DSS must use original fine Orion tiles and the original overview elsewhere.");
        if (orbital.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the real DSS sky survey.");
        int fine = detail.DetailPatches, coarse = overview.OverviewPatches;

        var site = new EarthSite(25, 0, 1.2);
        var sample = session.World.Ephemeris.Sample(epoch);
        var initial = EarthSurfaceLighting.Sample(sample, site, session.DeltaT);
        double longitude = Math.IEEERemainder(83.82 - initial.EarthRotationRadians * 180 / Math.PI, 360);
        site = site with { LongitudeDegrees = longitude };
        var lighting = EarthSurfaceLighting.Sample(sample, site, session.DeltaT);
        var horizontal = EarthObserving.HorizontalDirection(lighting, orion);
        if (lighting.SolarAltitudeRadians > -.30 || horizontal is not { AzimuthDegrees: { } azimuth, AltitudeDegrees: >= 30 } point)
            throw new InvalidOperationException("The DSS ground probe must see the Orion region under a dark sky.");
        var ground = new ObserverView(epoch, CameraMode.Surface, null, false)
        {
            EarthObserver = new EarthObserverCamera(site, azimuth, point.AltitudeDegrees) { Conditions = new(1) },
            FieldOfViewDegrees = 2.5,
            Sky = sky
        };
        var disabled = ground with { Sky = sky with { SurveyId = null } };
        string surface = Path.Combine(directory, "sky-survey-orion-from-earth.stscene");
        ScenarioFile.Save(surface, new([ground, disabled], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(surface))
            throw new InvalidOperationException("Could not open the two ground views.");
        using var enabled = await Screenshot();
        if (scene.Renderer(0).SurfaceSurveyCanvas is not { SurveyId: "dss", DetailPatches: > 0 } ||
            scene.Renderer(1).SurfaceSurveyCanvas?.SurveyId is not null)
            throw new InvalidOperationException("A local survey must render only in the chosen ground view.");
        if (enabled.SavePng(Path.ChangeExtension(surface, ".png")) != Error.Ok)
            throw new IOException("Could not capture the DSS layer through Earth's atmosphere.");
        string compare = Path.Combine(directory, "sky-survey-orion-disabled.stscene");
        ScenarioFile.Save(compare, new([disabled, disabled], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(compare))
            throw new InvalidOperationException("Could not open the unmodified ground sky.");
        using var plain = await Screenshot();
        int changed = 0;
        for (int y = 145; y < enabled.GetHeight() - 50; y += 4)
        for (int x = 400; x < (enabled.GetWidth() + 350) / 2 - 20; x += 4)
        {
            Color a = enabled.GetPixel(x, y), b = plain.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .03)
                changed++;
        }
        if (changed < 200)
            throw new InvalidOperationException($"The original DSS survey changed only {changed} ground-sky samples.");
        GD.Print($"Local DSS: {fine} fine Orion patches from order 5, {coarse} overview patches, {changed} ground-sky GPU samples, independent reload.");

        ObserverView Space(Vector3d direction)
        {
            var up = new Vector3d(0, 0, 1) - direction * direction.Z;
            var pose = new NavigationCamera(session.World.StellarOrigin + new Vector3d(0, 0, 4e8), 1000, 0, 0, null, false)
                { Placement = new NavigationPlacement(default, direction, up / up.Length) };
            return new(epoch, CameraMode.Solar, null, false) { Navigation = pose, Sky = sky, FieldOfViewDegrees = 2.5 };
        }

        static Vector3d Direction(float ra, float dec)
        {
            Vector3 point = BrightSkyData.Equatorial(ra, dec);
            var direction = new Vector3d(point.X, point.Y, point.Z);
            return direction / direction.Length;
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
