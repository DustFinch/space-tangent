// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseGalacticSky(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_GALACTIC_SKY_PROBE") != "1")
            return;
        var session = scene.Session!;
        var epoch = session[0].Epoch;
        var origin = session.World.StellarOrigin;
        Basis3d axes = GalacticLight.ToEquatorial;
        var sun = GalacticLight.SunKiloparsecs;
        var above = new Vector3d(0, 0, 40);
        var sky = new SkyLayerSettings(false, false, false, false) { DeepSky = true };
        double latitude = -4 * Math.PI / 180;
        Vector3d towardBulge = axes.X * Math.Cos(latitude) + axes.Z * Math.Sin(latitude);
        var inside = View(sun, towardBulge, axes.Z, 80);
        var outside = View(above, axes.Z * -1, axes.Y, 75);
        string path = Path.Combine(directory, "galactic-two-positions");
        ScenarioFile.Save(path + ".stscene", new([inside, outside], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path + ".stscene"))
            throw new InvalidOperationException("Could not load the galactic camera positions.");

        ulong deadline = Time.GetTicksMsec() + 25000;
        while (!scene.Renderer(0).GalacticSkyVisible || !scene.Renderer(1).GalacticSkyVisible)
        {
            if (Time.GetTicksMsec() > deadline)
                throw new TimeoutException("The two galactic observer maps did not publish.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (scene.Renderer(0).GalacticCache?.HasDust != true || scene.Renderer(0).GalacticSkyCenter != sun ||
            scene.Renderer(1).GalacticSkyCenter != above || !await scene.ReloadAddOns() ||
            !scene.Renderer(0).GalacticSkyVisible || !scene.Renderer(1).GalacticSkyVisible)
            throw new InvalidOperationException("The archived dust grid or per-view cache failed after reload.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var enabled = GetViewport().GetTexture().GetImage();
        if (enabled.SavePng(path + ".png") != Error.Ok)
            throw new IOException("Could not save both galactic views.");

        string disabled = Path.Combine(directory, "galactic-disabled.stscene");
        ScenarioFile.Save(disabled, new([inside with { Sky = default }, outside with { Sky = default }], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(disabled) || scene.Renderer(0).GalacticSkyVisible || scene.Renderer(1).GalacticSkyVisible)
            throw new InvalidOperationException("The deep-sky layer must control galactic visibility in both views.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var off = GetViewport().GetTexture().GetImage();
        if (off.SavePng(Path.ChangeExtension(disabled, ".png")) != Error.Ok)
            throw new IOException("Could not save the galactic layer control frame.");
        var site = new EarthSite(-24.627, -70.404, 2.635);
        JulianInstant groundEpoch = JulianInstant.FromJulianDate(2455755, TimeScale.Tdb);
        EarthObserverCamera? camera = null;
        for (int hour = 0; hour < 24; hour++, groundEpoch = groundEpoch.AddSeconds(3600))
        {
            var light = EarthSurfaceLighting.Sample(session.World.Ephemeris.Sample(groundEpoch), site, session.DeltaT);
            var center = EarthObserving.HorizontalDirection(light, towardBulge);
            if (light.SolarAltitudeRadians > -.26 || center is not { AltitudeDegrees: >= 25, AzimuthDegrees: { } azimuth } point)
                continue;
            camera = new EarthObserverCamera(site, azimuth, point.AltitudeDegrees) { Conditions = new(1) };
            break;
        }
        if (camera is null)
            throw new InvalidOperationException("The pinned Chilean night must place the Galactic Center above the horizon.");
        var surface = new ObserverView(groundEpoch, CameraMode.Surface, null, false)
            { EarthObserver = camera, FieldOfViewDegrees = 50, Sky = sky };
        string ground = Path.Combine(directory, "galactic-paranal.stscene");
        ScenarioFile.Save(ground, new([surface, surface with { Sky = default }], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(ground) || !scene.Renderer(0).GroundGalacticSkyVisible || scene.Renderer(1).GroundGalacticSkyVisible)
            throw new InvalidOperationException("The Galactic Center background must be independently selectable from Paranal.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var groundImage = GetViewport().GetTexture().GetImage();
        if (groundImage.SavePng(Path.ChangeExtension(ground, ".png")) != Error.Ok)
            throw new IOException("Could not save the Galactic Center viewed through the ground sky.");
        GD.Print("Galactic light: original extinction grid, independent Sun and external 40 kpc views, Paranal night, reload and layer toggle.");

        ObserverView View(Vector3d position, Vector3d forward, Vector3d up, double field)
        {
            Vector3d observer = origin + axes.Transform(position - sun) *
                (GalacticLight.KiloparsecLightYears * StellarEntry.KilometersPerLightYear);
            var horizontal = Vector3d.Cross(forward, up);
            var orthogonalUp = Vector3d.Cross(horizontal, forward);
            return new(epoch, CameraMode.Solar, null, false)
            {
                Navigation = new NavigationCamera(observer, 2000, 0, 0, null, false)
                    { Placement = new NavigationPlacement(default, forward, orthogonalUp / orthogonalUp.Length) },
                FieldOfViewDegrees = field,
                Sky = sky
            };
        }
    }
}
