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
    private async Task ExerciseZodiacalLight(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var paranal = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory");
        var start = TimeConversion.FromUtc(new(new(2024, 9, 1), 0, 0), TimeScale.Tdb);
        const double epsilon = 23.4392911 * Math.PI / 180;
        string profile = Path.Combine(directory, "archived-zodiacal.json");
        File.WriteAllText(profile, """{"Site":{},"SkyLayers":64,"ZodiacalLight":true}""");
        for (int hour = 0; hour < 48; hour++)
        {
            var epoch = start.AddSeconds(hour * 3600);
            var snapshot = session.World.Graph.Evaluate(epoch);
            var solar = snapshot.SolarSystem!.Value;
            var lighting = EarthSurfaceLighting.Sample(solar, paranal.ObserverSite, session.DeltaT);
            if (lighting.SolarAltitudeRadians > -.23)
                continue;
            Vector3d sun = (solar.Sun - solar.Earth).PositionKilometers;
            double longitude = Math.Atan2(sun.Y * Math.Cos(epsilon) + sun.Z * Math.Sin(epsilon), sun.X);
            var (sin, cos) = Math.SinCos(longitude + Math.PI / 4);
            var direction = new Vector3d(cos, sin * Math.Cos(epsilon), sin * Math.Sin(epsilon));
            if (EarthObserving.HorizontalDirection(lighting, direction) is not { } horizon ||
                horizon.AltitudeDegrees is < 12 or > 50 || horizon.AzimuthDegrees is not { } azimuth)
                continue;

            var ground = new ObserverView(epoch, CameraMode.Surface, null, false)
            {
                EarthObserver = new(paranal.ObserverSite, azimuth, horizon.AltitudeDegrees) { Conditions = new(1) },
                FieldOfViewDegrees = 12
            };
            var sky = LegacyObserverProfiles.Read(profile, ground, session.DeltaT).Observer.Sky;
            if (!sky.ZodiacalLight)
                throw new InvalidOperationException("The archived zodiacal setting was not imported.");

            var up = new Vector3d(0, -Math.Sin(epsilon), Math.Cos(epsilon));
            var orbit = ground with
            {
                Mode = CameraMode.Solar,
                Navigation = new(solar.Earth.PositionKilometers, 30000, 0, 0, null, false)
                {
                    Placement = new(up * 30000, direction, up)
                }
            };
            string path = Path.Combine(directory, "zodiacal-two-views.stscene");
            ScenarioFile.Save(path, new([ground with { Sky = sky }, orbit with { Sky = sky }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
                !session[0].Sky.ZodiacalLight || !session[1].Sky.ZodiacalLight)
                throw new InvalidOperationException("The zodiacal settings did not survive reloading the scenario and add-ons.");
            await WaitFrames();
            using var groundLit = ViewImage(0);
            using var orbitLit = ViewImage(1);
            if (groundLit.SavePng(Path.Combine(directory, "zodiacal-ground.png")) != Error.Ok ||
                orbitLit.SavePng(Path.Combine(directory, "zodiacal-orbit.png")) != Error.Ok)
                throw new IOException("Could not capture the zodiacal light.");

            path = Path.Combine(directory, "zodiacal-controls.stscene");
            var disabled = sky with { ZodiacalLight = false };
            var toggle = (CheckButton)scene.FindChild("ZodiacalLightLayer", recursive: true, owned: false);
            if (toggle.Disabled || !toggle.ButtonPressed)
                throw new InvalidOperationException("The layer control did not reflect the imported setting.");
            toggle.ButtonPressed = false;
            if (session[0].Sky.ZodiacalLight || !session[1].Sky.ZodiacalLight)
                throw new InvalidOperationException("The layer control must only change the active view.");
            await WaitFrames();
            using var groundDark = ViewImage(0);
            using var orbitStillLit = ViewImage(1);
            double groundDelta = Brightness(groundLit) - Brightness(groundDark);
            if (groundDelta < .002 || Math.Abs(Brightness(orbitLit) - Brightness(orbitStillLit)) > .001)
                throw new InvalidOperationException($"Ground zodiacal difference {groundDelta:F5} or independent orbit view is incorrect.");

            var distant = orbit.Navigation!.Value with
            {
                AnchorKilometers = solar.Sun.PositionKilometers - sun * 1.6
            };
            ScenarioFile.Save(path, new([ground with { Sky = disabled }, orbit with { Sky = disabled }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not apply the orbital toggle.");
            await WaitFrames();
            using var orbitDark = ViewImage(1);
            double orbitDelta = Brightness(orbitLit) - Brightness(orbitDark);
            if (orbitDelta < .002)
                throw new InvalidOperationException($"Orbital zodiacal difference {orbitDelta:F5} is too small.");

            ScenarioFile.Save(path, new([ground with { Sky = sky, EarthObserver = ground.EarthObserver!.Value with { Conditions = new(9) } },
                orbit with { Sky = sky, Navigation = distant }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not apply the bright-site and distant-view cases.");
            await WaitFrames();
            var groundMaterial = scene.Renderer(0).GetChildren().OfType<CanvasLayer>()
                .SelectMany(layer => layer.GetChildren().OfType<ColorRect>())
                .Select(rect => rect.Material).OfType<ShaderMaterial>()
                .Single(shader => shader.Shader.ResourcePath.EndsWith("/earth_observer.gdshader", StringComparison.Ordinal));
            var orbitMaterial = scene.Renderer(1).GetChildren().OfType<CanvasLayer>()
                .SelectMany(layer => layer.GetChildren().OfType<ColorRect>())
                .Select(rect => rect.Material).OfType<ShaderMaterial>()
                .Single(shader => shader.Shader.ResourcePath.EndsWith("/galactic_background.gdshader", StringComparison.Ordinal));
            if (groundMaterial.GetShaderParameter("show_zodiacal_light").AsBool() ||
                orbitMaterial.GetShaderParameter("show_zodiacal_light").AsBool())
                throw new InvalidOperationException("The glow must be suppressed at Bortle 9 and beyond 1.3 au.");
            GD.Print($"Zodiacal light GPU: original map, Paranal ground +{groundDelta:F5}, Earth orbit +{orbitDelta:F5}, independent toggle, Bortle 9 and 1.6 au suppression.");
            return;
        }
        throw new InvalidOperationException("No evening ecliptic direction was found at Paranal.");

        Image ViewImage(int view) => scene.Renderer(view).GetViewport().GetTexture().GetImage();
        async Task WaitFrames()
        {
            for (int index = 0; index < 4; index++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }

        static double Brightness(Image image)
        {
            int width = image.GetWidth(), height = image.GetHeight();
            double sum = 0;
            int count = 0;
            for (int y = height / 3; y < 2 * height / 3; y += 4)
                for (int x = width / 3; x < 2 * width / 3; x += 4)
                {
                    sum += image.GetPixel(x, y).R;
                    count++;
                }
            return sum / count;
        }
    }
}
