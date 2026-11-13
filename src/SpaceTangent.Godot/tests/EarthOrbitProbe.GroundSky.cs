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

    private async Task ExercisePointerCoordinates(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var time = TimeConversion.FromUtc(new(new(2024, 3, 10), 4, 30), TimeScale.Tdb);
        var paranal = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory");
        var ordinary = new ObserverView(time, CameraMode.Surface, null, false)
        {
            EarthObserver = new EarthObserverCamera(paranal.ObserverSite, 120, 55)
        };
        string boundariesPath = LocalDataPaths.Find("SPACETANGENT_CONSTELLATION_BOUNDARIES", "sky/boundaries.txt");
        using var input = File.OpenText(boundariesPath);
        var boundaries = SkyBoundaryMap.Read(input);
        if (boundaries.Segments.Count != 12959)
            throw new InvalidOperationException("The original constellation boundary rendering segments changed.");
        static SpaceTangent.Contracts.Vector3d Ray(double hours, double declination)
        {
            double ra = hours * Math.PI / 12;
            double dec = declination * Math.PI / 180;
            return new(Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
        }
        foreach (var (hours, degrees, expected) in new[]
        {
            (18.6156, 38.7837, "LYR"), (2.5303, 89.2641, "UMI"), (6.7525, -16.7161, "CMA"),
            (5.2423, -8.2016, "ORI"), (16.4901, -26.4320, "SCO"), (12.4433, -63.0991, "CRU"),
            (0.1398, 29.0904, "AND"), (3.0, -89.9, "OCT"), (18.0, 66.56, "DRA"),
            (15.5, 3.0, "SER1"), (18.3, -2.0, "SER2")
        })
            if (boundaries.At(Ray(hours, degrees)) != expected)
                throw new InvalidOperationException($"Expected constellation {expected}.");
        for (double dec = -87.5; dec < 90; dec += 5)
            for (double hours = .1; hours < 24; hours += 1d / 3)
                if (boundaries.At(Ray(hours, dec)) is null)
                    throw new InvalidOperationException($"Missing constellation at {hours} h, {dec}°.");

        string profile = Path.Combine(directory, "archived-pointer-coordinates.json");
        File.WriteAllText(profile, """{"Site":{},"PointerCoordinates":true}""");
        var imported = LegacyObserverProfiles.Read(profile, ordinary, session.DeltaT).Observer;
        var enabled = ordinary with { Sky = imported.Sky };
        string path = Path.Combine(directory, "paranal-pointer-coordinates.stscene");
        ScenarioFile.Save(path, new([enabled, ordinary], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            !session[0].Sky.PointerCoordinates || session[1].Sky.PointerCoordinates)
            throw new InvalidOperationException("The imported pointer setting must survive reload for only the first view.");

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var left = (SubViewportContainer)scene.FindChild("ViewCanvas0", recursive: true, owned: false);
        var right = (SubViewportContainer)scene.FindChild("ViewCanvas1", recursive: true, owned: false);
        static void Move(Control canvas, Vector2 position) =>
            canvas.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseMotion { Position = position });
        Move(left, left.Size / 2);
        Move(right, right.Size / 2);
        if (left.GetNodeOrNull<Label>("PointerCoordinates") is not { Visible: true } label ||
            !label.Text.Contains("(J2000)  · ", StringComparison.Ordinal) ||
            !label.Text.EndsWith("A 120°00′00″  h +55°00′00″", StringComparison.Ordinal) ||
            right.GetNodeOrNull<Label>("PointerCoordinates") is { Visible: true })
            throw new InvalidOperationException($"Expected the pointing direction only on the enabled view: {left.GetNodeOrNull<Label>("PointerCoordinates")?.Text}");

        string groundText = label.Text;
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var frame = GetViewport().GetTexture().GetImage();
        if (frame.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the pointer coordinates.");

        Move(left, new Vector2(-1, -1));
        if (label.Visible)
            throw new InvalidOperationException("The readout must hide outside the viewport.");
        Move(left, left.Size / 2);
        left.EmitSignal(Control.SignalName.MouseExited);
        if (label.Visible)
            throw new InvalidOperationException("The readout must hide when the pointer leaves the viewport.");
        Move(left, left.Size / 2);
        GetWindow().PushInput(new InputEventKey { Keycode = Key.C, AltPressed = true, Pressed = true });
        GetWindow().PushInput(new InputEventKey { Keycode = Key.C, AltPressed = true, Pressed = false });
        if (session[0].Sky.PointerCoordinates || session[1].Sky.PointerCoordinates || label.Visible)
            throw new InvalidOperationException("Alt+C must switch off the readout in the active view.");
        var earth = SpaceTangent.Core.Ephemerides.SolarSystemSample.EarthId;
        var snapshot = session.World.Graph.Evaluate(time);
        if (!snapshot.TryState(earth, out var state))
            throw new InvalidOperationException("Earth is missing from the perspective test.");
        var orbit = enabled with
        {
            Mode = CameraMode.Solar,
            Navigation = new(state.PositionKilometers, 26000, 0, 0, earth, true)
        };
        path = Path.Combine(directory, "perspective-pointer-coordinates.stscene");
        ScenarioFile.Save(path, new([orbit, ordinary], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not load the perspective test.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        left = (SubViewportContainer)scene.FindChild("ViewCanvas0", recursive: true, owned: false);
        Move(left, left.Size / 2);
        if (left.GetNodeOrNull<Label>("PointerCoordinates") is not { Visible: true } perspective ||
            !perspective.Text.EndsWith("(J2000)  · Orion", StringComparison.Ordinal) ||
            perspective.Text.Contains("  A ", StringComparison.Ordinal))
            throw new InvalidOperationException("The perspective view must report J2000 coordinates without a horizon.");
        GD.Print($"Pointer coordinates GPU: Paranal, perspective and independent views after reload; {groundText}; {perspective.Text}.");
        GD.Print("Original constellation boundaries: 12959 segments, 11 landmark directions and 2592 sky samples.");
    }

    private async Task ExerciseGroundPlanets(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var world = session.World;
        var paranal = world.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory");
        var site = paranal.ObserverSite;
        var venus = SpaceTangent.Core.Ephemerides.SolarSystemSample.VenusId;
        var start = JulianInstant.FromJulianDate(2460690, TimeScale.Tdb);
        for (int hour = 0; hour < 24 * 45; hour++)
        {
            var epoch = start.AddSeconds(hour * 3600);
            var snapshot = world.Graph.Evaluate(epoch);
            if (snapshot.SolarSystem is not { } solar || !snapshot.TryState(venus, out var planet))
                continue;
            var lighting = EarthSurfaceLighting.Sample(solar, site, session.DeltaT);
            if (lighting.SolarAltitudeRadians > -.16 ||
                EarthObserving.Horizontal(lighting, (planet - solar.Earth).PositionKilometers) is not
                    { AzimuthDegrees: { } heading, AltitudeDegrees: > 12 and < 75 } point)
                continue;
            var camera = new EarthObserverCamera(site, heading, point.AltitudeDegrees);
            var standard = new ObserverView(epoch, CameraMode.Surface, null, false)
                { EarthObserver = camera, FieldOfViewDegrees = 2, Sky = new() { HideClouds = true } };
            string profile = Path.Combine(directory, "archived-planet-scale.json");
            File.WriteAllText(profile, """{"DeltaT":"MorrisonStephenson2004","PlanetScale":10}""");
            var imported = LegacyObserverProfiles.Read(profile, standard, session.DeltaT).Observer;
            var scaled = standard with { EarthObserver = camera with { PlanetScale = imported.EarthObserver?.PlanetScale } };
            string path = Path.Combine(directory, "paranal-venus-scale.stscene");
            ScenarioFile.Save(path, new([scaled, standard], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != scaled || session[1] != standard)
                throw new InvalidOperationException("The original planet scale must survive with independent ground observers.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var large = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            using var small = scene.Renderer(1).GetViewport().GetTexture().GetImage();
            using var comparison = GetViewport().GetTexture().GetImage();
            int first = Bright(large), second = Bright(small);
            if (comparison.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not save the ground Venus scale comparison.");
            if (first < 15 || second < 1 || first <= second * 5)
                throw new InvalidOperationException($"Expected enlarged Venus at the same Paranal epoch, got {first}/{second} samples.");
            GD.Print($"Ground planets GPU: original Venus at Paranal, 10x/1x lit samples {first}/{second}, both views restored.");
            await ExerciseGroundSaturnRings(scene, site, directory);
            return;
        }
        throw new InvalidOperationException("No dark Venus observation was found for the original Paranal site.");

        static int Bright(Image image)
        {
            int count = 0;
            Vector2I center = image.GetSize() / 2;
            for (int y = center.Y - 45; y <= center.Y + 45; y++)
            for (int x = center.X - 45; x <= center.X + 45; x++)
                if (image.GetPixel(x, y).R > .5f)
                    count++;
            return count;
        }
    }

    private async Task ExerciseGroundSaturnRings(SolarSystemScene scene, EarthSite site, string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_RING_TEXTURES")))
            return;
        var session = scene.Session!;
        var start = JulianInstant.FromJulianDate(2460575.5, TimeScale.Tdb);
        var saturn = PlanetCatalogue.Find(PlanetCatalogue.SaturnId)!;
        for (int hour = 0; hour < 48; hour++)
        {
            var epoch = start.AddSeconds(hour * 3600);
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (snapshot.SolarSystem is not { } solar || !snapshot.TryState(saturn.Id, out var state))
                continue;
            var lighting = EarthSurfaceLighting.Sample(solar, site, session.DeltaT);
            var relative = (state - solar.Earth).PositionKilometers;
            if (lighting.SolarAltitudeRadians > -.20 ||
                EarthObserving.Horizontal(lighting, relative) is not { AzimuthDegrees: { } azimuth, AltitudeDegrees: > 30 } point)
                continue;
            var camera = new EarthObserverCamera(site, azimuth, point.AltitudeDegrees) { PlanetScale = 10 };
            var observer = new ObserverView(epoch, CameraMode.Surface, saturn.Id, false)
                { EarthObserver = camera, FieldOfViewDegrees = 2, Sky = new() { HideClouds = true } };
            string path = Path.Combine(directory, "paranal-saturn-rings.stscene");
            ScenarioFile.Save(path, new([observer], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the original ground Saturn scenario.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            var center = image.GetSize() / 2;
            double radius = image.GetHeight() * 10 * saturn.RadiusKilometers /
                (2 * Math.Tan(Math.PI / 180) * (relative - lighting.PositionKilometers).Length);
            int ringPixels = 0;
            for (int y = center.Y - 70; y < center.Y + 70; y++)
            for (int x = center.X - 70; x < center.X + 70; x++)
            {
                double dx = x + .5 - image.GetWidth() * .5, dy = y + .5 - image.GetHeight() * .5;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > 1.15 * radius && distance < 2.34 * radius && image.GetPixel(x, y).R > .3f)
                    ringPixels++;
            }
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture ground Saturn.");
            if (ringPixels < 12)
                throw new InvalidOperationException($"Expected the original rings outside Saturn's globe, got {ringPixels} pixels.");
            GD.Print($"Ground Saturn rings GPU: {ringPixels} illuminated ring pixels outside the disc at Paranal.");
            var material = scene.Renderer(0).GetChildren().OfType<CanvasLayer>()
                .SelectMany(layer => layer.GetChildren().OfType<ColorRect>())
                .Select(rect => rect.Material).OfType<ShaderMaterial>()
                .Single(shader => shader.Shader.ResourcePath.EndsWith("/earth_observer.gdshader", StringComparison.Ordinal));
            if (material.GetShaderParameter("ground_planet_maps").AsGodotObject() is not Texture2DArray maps ||
                maps.GetLayers() != PlanetCatalogue.Planets.Count)
                throw new InvalidOperationException("The ground view must bind the original planetary maps.");
            material.SetShaderParameter("use_ground_planet_maps", false);
            try
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var flat = scene.Renderer(0).GetViewport().GetTexture().GetImage();
                int changed = 0;
                for (int y = center.Y - 50; y < center.Y + 50; y++)
                for (int x = center.X - 50; x < center.X + 50; x++)
                {
                    double dx = x + .5 - image.GetWidth() * .5, dy = y + .5 - image.GetHeight() * .5;
                    if (dx * dx + dy * dy < .81 * radius * radius &&
                        Math.Abs(image.GetPixel(x, y).R - flat.GetPixel(x, y).R) > .03)
                        changed++;
                }
                if (flat.SavePng(Path.Combine(directory, "paranal-saturn-without-map.png")) != Error.Ok || changed < 25)
                    throw new InvalidOperationException($"Expected the original Saturn surface to change the disc, got {changed} pixels.");
                GD.Print($"Ground Saturn texture GPU: {changed} changed interior pixels with the same pose and rings.");
            }
            finally
            {
                material.SetShaderParameter("use_ground_planet_maps", true);
            }
            return;
        }
        throw new InvalidOperationException("No dark Saturn observation was found at Paranal.");
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
