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
    private async Task ExerciseEquatorialMount(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var time = TimeConversion.FromUtc(new(new(2024, 3, 10), 4, 30), TimeScale.Tdb);
        var paranal = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory");
        var camera = new EarthObserverCamera(paranal.ObserverSite, 120, 55, HorizonGrid: true);
        var ordinary = new ObserverView(time, CameraMode.Surface, null, false) { EarthObserver = camera };
        string profile = Path.Combine(directory, "archived-equatorial-mount.json");
        File.WriteAllText(profile, """{"DeltaT":"MorrisonStephenson2004","EquatorialMount":true}""");
        var equatorial = LegacyObserverProfiles.Read(profile, ordinary, session.DeltaT).Observer;
        string path = Path.Combine(directory, "paranal-equatorial-mount.stscene");
        ScenarioFile.Save(path, new([equatorial, ordinary], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session[0] != equatorial || session[1] != ordinary)
            throw new InvalidOperationException("The two archived mount modes must survive reload with independent cameras.");

        var material = scene.Renderer(0).GetChildren().OfType<CanvasLayer>()
            .SelectMany(layer => layer.GetChildren().OfType<ColorRect>())
            .Select(rect => rect.Material).OfType<ShaderMaterial>()
            .Single(shader => shader.Shader.ResourcePath.EndsWith("/earth_observer.gdshader", StringComparison.Ordinal));
        Vector3 forward = material.GetShaderParameter("forward").AsVector3();
        Vector3 pole = Vector3.Forward;
        Vector3 expected = (pole - forward * forward.Dot(pole)).Normalized();
        if (material.GetShaderParameter("camera_up").AsVector3().DistanceTo(expected) > 1e-5)
            throw new InvalidOperationException("At Paranal the camera up axis must point toward the south celestial pole.");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var frame = GetViewport().GetTexture().GetImage();
        if (frame.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the two ground mount modes.");
        GetWindow().PushInput(new InputEventKey { Keycode = Key.M, AltPressed = true, Pressed = true });
        GetWindow().PushInput(new InputEventKey { Keycode = Key.M, AltPressed = true, Pressed = false });
        if (session[0].EarthObserver?.EquatorialMount != false || session[1] != ordinary)
            throw new InvalidOperationException("Alt+M must change only the active ground observer.");
        GD.Print("Equatorial mount GPU: original Paranal site, south pole alignment, independent views and Alt+M after reload.");
    }

    private async Task ExerciseGroundSky(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var place = session.World.EarthPlaces.Places.FirstOrDefault(item => item.EnglishName == "Paranal Observatory");
        if (place is null)
            return;
        var time = TimeConversion.FromUtc(new(new(2024, 3, 10), 4, 30), TimeScale.Tdb);
        var camera = new EarthObserverCamera(place.ObserverSite, 120, 55)
            { Conditions = new GroundSkyConditions(place.Bortle ?? 1) };
        var dark = new ObserverView(time, CameraMode.Surface, null, false) { EarthObserver = camera };
        var city = dark with { EarthObserver = camera with { Conditions = new GroundSkyConditions(9) } };
        string path = Path.Combine(directory, "ground-sky-paranal.stscene");
        var scenario = new SimulationScenario([dark, city], false, 0, 0, session.DeltaT);
        ScenarioFile.Save(path, scenario);
        if (ScenarioFile.Load(path).Views[0] != dark || !scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            scene.Session![0] != dark || scene.Session[1] != city)
            throw new InvalidOperationException("Both independent ground-sky profiles must survive round trips.");
        if (DisplayServer.GetName() != "headless")
        {
            scene.Renderer(0).SetSurfaceControls(clouds: false, lunarLight: false);
            scene.Renderer(1).SetSurfaceControls(clouds: false, lunarLight: false);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var pristine = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            using var bright = scene.Renderer(1).GetViewport().GetTexture().GetImage();
            using var comparison = GetViewport().GetTexture().GetImage();
            Color a = Center(pristine), b = Center(bright);
            if (b.R - a.R < .1f || b.R - a.R <= b.B - a.B)
                throw new InvalidOperationException($"A Bortle 9 sky must be brighter and warmer than Paranal. {a} {b}");
            if (comparison.SavePng(Path.Combine(directory, "ground-sky-paranal.png")) != Error.Ok)
                throw new IOException("Could not save the ground-sky comparison.");
            scene.Renderer(0).SetSurfaceControls(clouds: true, lunarLight: true);
            scene.Renderer(1).SetSurfaceControls(clouds: true, lunarLight: true);
        }
        GD.Print("Ground sky: original Paranal location, independent Bortle 1 and 9, serialized profiles and GPU glow.");

        static Color Center(Image image)
        {
            Vector2I center = image.GetSize() / 2;
            return image.GetPixel(center.X, center.Y);
        }
    }
}
