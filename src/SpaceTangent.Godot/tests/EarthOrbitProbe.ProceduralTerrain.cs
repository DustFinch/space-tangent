// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseProceduralTerrain(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROCEDURAL_TERRAIN_PROBE") != "1")
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

        GD.Print(FormattableString.Invariant(
            $"Procedural terrain: Shift+G, {highClearance:F3}/{nearClearance:F3} km, LOD {high}/{near}, {highPixels}/{nearPixels}/{obliquePixels} lit samples including oblique; two epochs and Delta T restored."));

        double Clearance(NavigationCamera pose)
        {
            if (pose.Placement is not { BodyFixed: true } fixedPose)
                throw new InvalidOperationException("Surface camera must retain its body-fixed placement.");
            Vector3d offset = fixedPose.OffsetKilometers;
            var native = new Vector3d(offset.X, offset.Z, -offset.Y) / offset.Length;
            return offset.Length - terrain.GroundRadiusKilometers(native);
        }

        async Task<int> Capture(string name)
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
            if (colored < 100)
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
