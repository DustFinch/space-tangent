// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseProceduralTerrain(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROCEDURAL_TERRAIN_PROBE") is not ("1" or "only"))
            return;
        var session = scene.Session!;
        ObjectId target = DemoWorlds.Aridus;
        var body = DemoWorlds.Find(target)!;
        var terrain = new ProceduralTerrain(DemoWorlds.CreateSurface(body)!, body.PolarRadiusKilometers);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var secondEpoch = epoch.AddSeconds(86400);
        var first = Observer(target, epoch);
        var second = Observer(DemoWorlds.Glacies, secondEpoch);
        const DeltaTModel deltaT = DeltaTModel.MorrisonStephenson2004;
        string path = Path.Combine(directory, "procedural-terrain.stscene");
        ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
        if (!scene.LoadScenario(path))
            throw new IOException("Could not load the two procedural observers.");

        GetWindow().PushInput(new InputEventKey { Keycode = Key.G, ShiftPressed = true, Pressed = true });
        GetWindow().PushInput(new InputEventKey { Keycode = Key.G, ShiftPressed = true, Pressed = false });
        if (!session.IsFlying(0))
            throw new InvalidOperationException("Shift+G did not initiate the procedural surface flight.");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (session.IsFlying(0) || scene.Renderer(0).DemoTerrainLevel(target) < 0)
        {
            if (timer.Elapsed.TotalSeconds > 25)
                throw new TimeoutException("The elevated procedural terrain did not render.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        int high = scene.Renderer(0).DemoTerrainLevel(target);
        double highClearance = Clearance(session[0].Navigation!.Value);
        if (highClearance is < 90 or > 115 || session[1] != second)
            throw new InvalidOperationException($"Surface flight changed the second observer or arrived at {highClearance:F3} km.");
        int highPixels = await Capture("procedural-terrain-elevated");

        if (!session.AdjustCamera(0, 0, 0, .01))
            throw new InvalidOperationException("Could not descend to the procedural surface.");
        string closePath = Path.Combine(directory, "procedural-terrain-close.stscene");
        ScenarioFile.Save(closePath, new([session[0], second], false, 0, 0, deltaT));
        if (!scene.LoadScenario(closePath))
            throw new IOException("Could not load the near procedural observers.");

        timer.Restart();
        while (scene.Renderer(0).DemoTerrainLevel(target) < high + 4)
        {
            if (timer.Elapsed.TotalSeconds > 30)
                throw new TimeoutException($"Terrain remained at level {scene.Renderer(0).DemoTerrainLevel(target)} after descending from level {high}.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        var close = session[0];
        int near = scene.Renderer(0).DemoTerrainLevel(target);
        double nearClearance = Clearance(close.Navigation!.Value);
        int nearPixels = await Capture("procedural-terrain-near");
        if (Math.Abs(nearClearance - .05) > 1e-6 || !await scene.ReloadAddOns() ||
            session[0] != close || session[1] != second || session.DeltaT != deltaT)
            throw new InvalidOperationException($"Terrain clearance or reload failed at {nearClearance:F6} km.");
        var surfacePose = close.Navigation.Value;
        var local = surfacePose.Placement!.Value;
        double angle = 58 * Math.PI / 180;
        var direction = local.Forward * Math.Cos(angle) + local.Up * Math.Sin(angle);
        var up = local.Up * Math.Cos(angle) - local.Forward * Math.Sin(angle);
        double obliqueRange = surfacePose.RangeKilometers + 2;
        var oblique = surfacePose with
        {
            RangeKilometers = obliqueRange,
            Placement = local with
            {
                Forward = direction, Up = up,
                OffsetKilometers = local.OffsetKilometers * (obliqueRange / surfacePose.RangeKilometers)
            }
        };
        string obliquePath = Path.Combine(directory, "procedural-terrain-oblique.stscene");
        ScenarioFile.Save(obliquePath, new([close with { Navigation = oblique }, second], false, 0, 0, deltaT));
        if (!scene.LoadScenario(obliquePath))
            throw new IOException("Could not load the oblique terrain observer.");
        timer.Restart();
        while (scene.Renderer(0).DemoTerrainLevel(target) < high + 1)
        {
            if (timer.Elapsed.TotalSeconds > 25)
                throw new TimeoutException("The oblique procedural terrain did not render.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        int obliquePixels = await Capture("procedural-terrain-oblique");
        var renderer = scene.Renderer(0);
        var nativeUp = new Vector3((float)local.OffsetKilometers.X, (float)local.OffsetKilometers.Z,
            (float)-local.OffsetKilometers.Y).Normalized();
        var tangentLight = Vector3.Up.Cross(nativeUp).Normalized();
        renderer.SetDemoTerrainProbeLight(target, (tangentLight + .018f * nativeUp).Normalized());
        scene.Seek(0, epoch);
        await Capture("procedural-terrain-shadows", requireLit: false);
        using var shaded = renderer.GetViewport().GetTexture().GetImage();
        renderer.SetDemoTerrainShadows(target, false);
        await Capture("procedural-terrain-no-shadows", requireLit: false);
        using var unshaded = renderer.GetViewport().GetTexture().GetImage();
        renderer.SetDemoTerrainShadows(target, true);
        renderer.SetDemoTerrainProbeLight(target, null);
        int darkened = 0;
        double strongest = 0;
        for (int y = 0; y < shaded.GetHeight(); y += 2)
        for (int x = 0; x < shaded.GetWidth(); x += 2)
        {
            double difference = unshaded.GetPixel(x, y).R - shaded.GetPixel(x, y).R;
            strongest = Math.Max(strongest, difference);
            if (difference > .008)
                darkened++;
        }
        if (darkened < 8)
            throw new InvalidOperationException($"Expected terrain shadows at the matched camera. Darkened samples {darkened}, peak delta {strongest:F4}.");
        GD.Print(FormattableString.Invariant($"Terrain shadows: {darkened} darkened samples, peak red delta {strongest:F4}."));

        GD.Print(FormattableString.Invariant(
            $"Procedural terrain: Shift+G, {highClearance:F3}/{nearClearance:F3} km, LOD {high}/{near}, {highPixels}/{nearPixels}/{obliquePixels} lit samples including oblique; two epochs and Delta T restored."));

        string profile = Path.Combine(directory, "archived-terrain-settings.json");
        File.WriteAllText(profile, """{"DeltaT":"MorrisonStephenson2004","Terrain":false}""");
        var withoutTerrain = LegacyObserverProfiles.Read(profile, close, deltaT).Observer;
        var disabled = close with { Sky = withoutTerrain.Sky };
        string disabledPath = Path.Combine(directory, "procedural-terrain-hidden.stscene");
        ScenarioFile.Save(disabledPath, new([disabled, second], false, 0, 0, deltaT));
        if (!scene.LoadScenario(disabledPath) || !await scene.ReloadAddOns() ||
            session[0] != disabled || session[1] != second ||
            scene.Renderer(0).DemoTerrainLevel(target) >= 0 || !scene.Renderer(0).DemoWorldVisible(target))
            throw new InvalidOperationException("The archived Terrain preference must preserve the globe and both camera poses.");
        await Capture("procedural-terrain-hidden", requireLit: false);
        GD.Print("Terrain settings GPU: original Aridus globe remains visible without its terrain after reload.");

        if (scene.Renderer(0).Terrain is not null && scene.Renderer(1).MarsTerrain is not null)
        {
            var moon = new ObserverView(epoch, CameraMode.LunarTerrain, null, false) { Terrain = LunarTerrainCamera.Copernicus };
            var marsEpoch = Enumerable.Range(0, 25).Select(hour => epoch.AddSeconds(3600 * hour)).MaxBy(at =>
            {
                var snapshot = session.World.Graph.Evaluate(at);
                snapshot.TryState(PlanetCatalogue.MarsId, out var state);
                snapshot.TryOrientation(PlanetCatalogue.MarsId, out var orientation);
                var sun = orientation.ToWorld.Transpose().Transform((snapshot.SolarSystem!.Value.Sun - state).PositionKilometers);
                double latitude = 18.65 * Math.PI / 180, longitude = -133.8 * Math.PI / 180;
                var olympus = new Vector3d(Math.Cos(latitude) * Math.Cos(longitude),
                    Math.Cos(latitude) * Math.Sin(longitude), Math.Sin(latitude));
                // Avoid both the night side and a flat overhead illumination for this contrast check.
                return -Math.Abs(Vector3d.Dot(sun / sun.Length, olympus) - .55);
            });
            var mars = new ObserverView(marsEpoch, CameraMode.MarsTerrain, null, false);
            string terrainPath = Path.Combine(directory, "lunar-mars-relief.stscene");
            ScenarioFile.Save(terrainPath, new([moon, mars], false, 0, 0, deltaT));
            if (!scene.LoadScenario(terrainPath))
                throw new InvalidOperationException("Could not open the original lunar and Mars terrain.");
            using var relief = await Frame();
            string smoothPath = Path.Combine(directory, "lunar-mars-smooth.stscene");
            ScenarioFile.Save(smoothPath, new([moon with { Sky = disabled.Sky }, mars with { Sky = disabled.Sky }], false, 0, 0, deltaT));
            if (!scene.LoadScenario(smoothPath) || scene.Renderer(0).Terrain?.Visible != true ||
                scene.Renderer(1).MarsTerrain?.Visible != true)
                throw new InvalidOperationException("The flat lunar and Mars views must keep their surfaces.");
            using var smooth = await Frame();
            int leftChanges = 0, rightChanges = 0;
            for (int y = 120; y < smooth.GetHeight() - 40; y += 4)
            for (int x = smooth.GetWidth() / 4; x < 4 * smooth.GetWidth() / 5; x += 4)
            {
                if (Math.Abs(relief.GetPixel(x, y).R - smooth.GetPixel(x, y).R) <= .015)
                    continue;
                if (x < smooth.GetWidth() / 2)
                    leftChanges++;
                else
                    rightChanges++;
            }
            if (relief.SavePng(Path.ChangeExtension(terrainPath, ".png")) != Error.Ok ||
                smooth.SavePng(Path.ChangeExtension(smoothPath, ".png")) != Error.Ok)
                throw new IOException("Could not save the terrain comparison frames.");
            if (leftChanges < 30 || rightChanges < 30)
                throw new InvalidOperationException($"Relief toggle must affect both surfaces. Changed samples {leftChanges}/{rightChanges}.");
            GD.Print($"Terrain settings GPU: original lunar and Mars maps, {leftChanges}/{rightChanges} changed samples with identical cameras.");

            async Task<Image> Frame()
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                return GetViewport().GetTexture().GetImage();
            }
        }

        double Clearance(NavigationCamera pose)
        {
            if (pose.Placement is not { BodyFixed: true } fixedPose)
                throw new InvalidOperationException("Surface camera must retain its body-fixed placement.");
            Vector3d offset = fixedPose.OffsetKilometers;
            var native = new Vector3d(offset.X, offset.Z, -offset.Y) / offset.Length;
            return offset.Length - terrain.GroundRadiusKilometers(native);
        }

        async Task<int> Capture(string name, bool requireLit = true)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var full = GetViewport().GetTexture().GetImage();
            if (full.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                throw new IOException($"Could not capture {name}.");
            using var image = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            int colored = 0;
            for (int y = image.GetHeight() / 4; y < 3 * image.GetHeight() / 4; y += 4)
            for (int x = image.GetWidth() / 4; x < 3 * image.GetWidth() / 4; x += 4)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.R > .12f && pixel.R > pixel.B * 1.2f)
                    colored++;
            }
            if (requireLit && colored < 100)
                throw new InvalidOperationException($"Expected an illuminated desert across the viewport in {name}, sampled {colored} pixels.");
            return colored;
        }

        ObserverView Observer(ObjectId id, JulianInstant at)
        {
            var snapshot = session.World.Graph.Evaluate(at);
            snapshot.TryState(id, out var state);
            snapshot.TryOrientation(id, out var rotation);
            Vector3d lit = DemoWorlds.Lights(snapshot, id)[0].Direction;
            Vector3d north = rotation.ToWorld.Z;
            Vector3d side = Vector3d.Cross(lit, north);
            Vector3d eye = lit + side * .17 + north * .2;
            eye /= eye.Length;
            Vector3d northOnScreen = north - eye * Vector3d.Dot(north, eye);
            double range = DemoWorlds.Find(id)!.RadiusKilometers * 3.6;
            return new(at, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                {
                    Placement = new NavigationPlacement(eye * range, eye * -1, northOnScreen / northOnScreen.Length)
                }
            };
        }
    }
}
