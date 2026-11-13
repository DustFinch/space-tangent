// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseApparentPlaces(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var epoch = SurfaceProjection.FullMoonEpoch;
        var moon = SpaceTangent.Core.Ephemerides.SolarSystemSample.MoonId;
        var sample = session.World.Graph.Evaluate(epoch).SolarSystem!.Value;
        var site = EarthObserverCamera.Moscow.Site;
        var light = EarthSurfaceLighting.Sample(sample, site, DeltaTModel.MorrisonStephenson2004);
        var geometric = light.EarthToMoon - light.PositionKilometers;
        var horizontal = EarthObserving.HorizontalDirection(light, geometric)!.Value;
        var camera = new EarthObserverCamera(site, horizontal.AzimuthDegrees!.Value, horizontal.AltitudeDegrees);
        var baseline = new ObserverView(epoch, CameraMode.Surface, moon, false)
        {
            EarthObserver = camera,
            FieldOfViewDegrees = 5
        };
        string profile = Path.Combine(directory, "archived-apparent-places.json");
        File.WriteAllText(profile, """{"Site":{},"ApparentPlaces":true,"Refraction":false}""");
        var corrected = LegacyObserverProfiles.Read(profile, baseline, session.DeltaT).Observer;
        var plain = corrected with { EarthObserver = corrected.EarthObserver!.Value with { ApparentPlaces = false } };
        string path = Path.Combine(directory, "apparent-and-geometric.stscene");
        ScenarioFile.Save(path, new([corrected, plain], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session[0].EarthObserver?.ApparentPlaces != true || session[1].EarthObserver?.ApparentPlaces != false ||
            session.DeltaT != DeltaTModel.MorrisonStephenson2004)
            throw new InvalidOperationException("Independent apparent places and ΔT must survive add-on reload.");

        Vector3 CorrectedMoon(int index) => scene.Renderer(index).GetChildren().OfType<CanvasLayer>()
            .SelectMany(layer => layer.GetChildren().OfType<ColorRect>())
            .Select(rect => rect.Material).OfType<ShaderMaterial>()
            .Single(shader => shader.Shader.ResourcePath.EndsWith("/earth_observer.gdshader", StringComparison.Ordinal))
            .GetShaderParameter("moon_seen").AsVector3();

        var a = CorrectedMoon(0);
        var b = CorrectedMoon(1);
        const double arcseconds = 180 * 3600 / Math.PI;
        double shift = a.Cross(b).Length() * arcseconds;
        var expected = geometric - sample.Moon.VelocityKilometersPerSecond * (geometric.Length / 299792.458);
        expected = expected / expected.Length + sample.Earth.VelocityKilometersPerSecond / 299792.458;
        expected /= expected.Length;
        if (shift is < .2 or > 30 || a.DistanceTo(new((float)expected.X, (float)expected.Y, (float)expected.Z)) > 2e-7)
            throw new InvalidOperationException($"Unexpected apparent Moon displacement {shift:F4} arcsec.");
        var beta = ApparentSky.ObserverBeta(sample.Earth);
        var transverse = Vector3d.Cross(beta, new(0, 0, 1));
        transverse /= transverse.Length;
        double starShift = Vector3d.Cross(transverse, ApparentSky.Aberrate(transverse, beta)).Length * arcseconds;
        if (starShift is < 19 or > 22)
            throw new InvalidOperationException($"Unexpected stellar aberration {starShift:F4} arcsec.");

        for (int frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the apparent and geometric Moon.");
        var toggle = (CheckButton)scene.FindChild("ApparentPlaces", recursive: true, owned: false);
        if (!toggle.ButtonPressed)
            throw new InvalidOperationException("The observer control did not reflect the archived setting.");
        toggle.ButtonPressed = false;
        if (session[0].EarthObserver?.ApparentPlaces != false || session[1] != plain ||
            CorrectedMoon(0).DistanceTo(CorrectedMoon(1)) > 2e-7)
            throw new InvalidOperationException("The apparent-place toggle must only change the active observer.");
        GD.Print($"Apparent places GPU: full Moon displaced {shift:F3} arcsec, stellar scale {starShift:F3} arcsec, independent toggle and ΔT after reload.");
    }
}
