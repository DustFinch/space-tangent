// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseDemoWorlds(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_DEMO_WORLDS_PROBE") != "1")
            return;
        var session = scene.Session!;
        if (!session.World.Catalogue.Search("Нова").Any(item => item.Id == DemoWorlds.Nova) ||
            session.World.Catalogue.Find(DemoWorlds.Selena)?.Parent != DemoWorlds.Nova)
            throw new InvalidOperationException("The original aliases and the moon's parent must resolve.");
        JulianInstant j2000 = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var eclipse = Enumerable.Range(0, 96).Select(index => j2000.AddSeconds((21 + index / 24d) * 86400))
            .MinBy(at => DemoWorlds.Lights(session.World.Graph.Evaluate(at), DemoWorlds.Nova).Sum(light => light.RelativeFlux));
        var calm = eclipse.AddSeconds(5 * 86400);
        var stars = DemoWorlds.Lights(session.World.Graph.Evaluate(eclipse), DemoWorlds.Nova);
        if (stars.Count != 2 || stars.Min(star => star.VisibleFraction) > .95)
            throw new InvalidOperationException("The selected Nova epoch must contain the authored binary eclipse.");
        foreach (var (left, right, name) in new[]
        {
            (DemoWorlds.Nova, DemoWorlds.Zephyr, "Нова и Зефир"),
            (DemoWorlds.Pyra, DemoWorlds.Glacies, "demo-pyra-glacies"),
            (DemoWorlds.Selena, DemoWorlds.Aridus, "demo-selena-aridus")
        })
        {
            var first = Observer(left, eclipse);
            var second = Observer(right, calm);
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not open {name}.");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!scene.Renderer(0).DemoWorldTextured(left) || !scene.Renderer(1).DemoWorldTextured(right))
            {
                if (timer.Elapsed.TotalSeconds > 25)
                    throw new TimeoutException($"Missing generated surfaces for {left.Value} and {right.Value}.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!scene.Renderer(0).DemoWorldVisible(left) || !scene.Renderer(1).DemoWorldVisible(right))
                throw new InvalidOperationException("The selected worlds must be visible and textured.");
            if (left == DemoWorlds.Nova)
            {
                if (!scene.Renderer(0).DemoAirVisible(left) || !scene.Renderer(1).DemoAirVisible(right) ||
                    !scene.Renderer(1).DemoRingsVisible(right))
                    throw new InvalidOperationException("The atmospheres and rings need the authored profiles.");
                if (!await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                    throw new InvalidOperationException("Independent demo observers must survive add-on reload.");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException($"Could not capture {name}.");
        }
        GD.Print(FormattableString.Invariant($"Demo worlds: six original procedural surfaces, two epochs, binary eclipse ({stars[0].VisibleFraction:F4}/{stars[1].VisibleFraction:F4}), atmospheres and rings, two cameras restored."));

        ObserverView Observer(ObjectId id, JulianInstant at)
        {
            var snapshot = session.World.Graph.Evaluate(at);
            snapshot.TryState(id, out var state);
            snapshot.TryOrientation(id, out var rotation);
            var body = DemoWorlds.Find(id)!;
            var lit = DemoWorlds.Lights(snapshot, id)[0].Direction;
            var side = Vector3d.Cross(lit, rotation.ToWorld.Z);
            var from = lit + side * .42 + rotation.ToWorld.Z * .4;
            from /= from.Length;
            var up = rotation.ToWorld.Z - from * Vector3d.Dot(from, rotation.ToWorld.Z);
            up /= up.Length;
            double range = body.RadiusKilometers * (body.RingOuterKilometers > 0 ? 7 : 3.6);
            return new(at, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(from * range, from * -1, up) }
            };
        }
    }
}
