// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredCometRendering(SolarSystemScene scene, string directory)
    {
        const string variable = "SPACETANGENT_SSC_ADDON_ROOT";
        string? oldRoot = System.Environment.GetEnvironmentVariable(variable);
        string saved = Path.Combine(directory, "before-comets.stscene");
        if (!scene.SaveScenario(saved)) throw new IOException("Could not preserve observers before comet acceptance.");
        string root = Path.Combine(directory, "authored-comets"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "comets.ssc"), """
            "Active Witness" "Sol" { Class "comet" Radius 2
                EllipticalOrbit { Epoch 2451545 Period 1.3975 SemiMajorAxis 1.25 Eccentricity 0.2 } }
            "Distant Witness" "Sol" { Class "comet" Radius 2 FixedPosition [1000000000 0 0] }
            """);
        try
        {
            System.Environment.SetEnvironmentVariable(variable, root);
            if (!await scene.ReloadAddOns()) throw new InvalidOperationException("Could not install the authored comet source.");
            var session = scene.Session!; var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb);
            ObjectId active = new("ssc/sol/active-witness"), distant = new("ssc/sol/distant-witness");
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(active, out var nucleus)) throw new InvalidOperationException("The authored conic must publish its nucleus state.");
            var relative = nucleus - snapshot.SolarSystem!.Value.Sun;
            Vector3d away = relative.PositionKilometers / relative.PositionKilometers.Length;
            Vector3d trail = away * Vector3d.Dot(relative.VelocityKilometersPerSecond, away) - relative.VelocityKilometersPerSecond;
            trail /= trail.Length;
            Vector3d normal = Vector3d.Cross(away, trail); normal /= normal.Length;
            double expectedLength = 1e15 / 149597870.7 * .4;
            var view = Observer(active, nucleus.PositionKilometers, expectedLength * 1.5);
            string path = Path.Combine(directory, "authored-comets.stscene");
            ScenarioFile.Save(path, new([view, view with { Sky = view.Sky with { HideCometTails = true } }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns()) throw new InvalidOperationException("Could not restore comet cameras and layer preferences.");
            if (scene.Renderer(0).RenderedComet(active) is not { } result ||
                scene.Renderer(1).RenderedComet(active) is not null || scene.Renderer(0).RenderedComet(distant) is not null ||
                Vector3d.Dot(result.Away, away) < 1 - 1e-12 || Vector3d.Dot(result.Trail, trail) < 1 - 1e-12 ||
                Math.Abs(result.Length - expectedLength) > .01 || result.ComaFade < .99)
                throw new InvalidOperationException("The comet must have anti-solar ions, retrograde dust, the analytic perihelion scale and an independent enabled layer.");
            using var visible = await Picture(0); using var hidden = await Picture(1);
            int changes = 0;
            for (int y = 0; y < Math.Min(visible.GetHeight(), hidden.GetHeight()); y += 2)
            for (int x = 0; x < Math.Min(visible.GetWidth(), hidden.GetWidth()); x += 2)
            {
                Color a = visible.GetPixel(x, y), b = hidden.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .03f) changes++;
            }
            if (changes < 12 || visible.SavePng(Path.Combine(directory, "authored-comet-visible.png")) != Error.Ok)
                throw new InvalidOperationException("The enabled comet layer must change visible GPU samples.");
            var close = Observer(active, nucleus.PositionKilometers, 10);
            ScenarioFile.Save(path, new([close, view], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || scene.Renderer(0).RenderedComet(active) is not { ComaFade: 0 })
                throw new InvalidOperationException("The coma must fade for a camera inside its physical display radius.");
            if (!snapshot.TryState(distant, out var far)) throw new InvalidOperationException("The distant authored nucleus is required.");
            ScenarioFile.Save(path, new([Observer(distant, far.PositionKilometers, 100), view], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || scene.Renderer(0).RenderedComet(distant) is not null)
                throw new InvalidOperationException("A comet beyond five AU must not show an active tail.");
            GD.Print($"Authored comet GPU: analytic perihelion length, anti-solar ions, retrograde dust, coma fade and independent layers; {changes} changed samples.");

            ObserverView Observer(ObjectId id, Vector3d anchor, double range) => new(epoch, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(anchor, range, 0, 0, id, true)
                    { Placement = new(normal * range, normal * -1, away) },
                Sky = new(true, false, false, false)
            };
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, oldRoot);
            if (!await scene.ReloadAddOns() || !scene.LoadScenario(saved))
                throw new InvalidOperationException("Could not restore the original add-ons and observers after comet acceptance.");
        }

        async Task<Image> Picture(int index)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return scene.Renderer(index).GetViewport().GetTexture().GetImage();
        }
    }
}
