// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionParticles(SolarSystemScene scene, string directory)
    {
        const string variable = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PARTICLE_GPU_ROOT") is not { Length: > 0 } folder)
            return;
        string? prior = System.Environment.GetEnvironmentVariable(variable);
        string absent = Directory.CreateDirectory(Path.Combine(directory, "without-particle-catalogues")).FullName;
        var id = new ObjectId("mission/tvashtar");
        var session = scene.Session!;
        var epoch = JulianInstant.FromJulianDate(2455755.0, TimeScale.Tdb);
        var before = JulianInstant.FromJulianDate(2447000, TimeScale.Tdb);
        string path = Path.Combine(directory, "mission-tvashtar.stscene");

        try
        {
            System.Environment.SetEnvironmentVariable(variable, folder);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("Could not install the original Tvashtar catalogue.");
            var frame = session.World.Graph.Evaluate(epoch);
            if (!frame.TryState(id, out var state) || !frame.TryOrientation(id, out var body))
                throw new InvalidOperationException("The original Tvashtar source must resolve its Io position and body axes.");
            Vector3d pole = body.ToWorld.Z;
            Vector3d lateral = body.ToWorld.X;
            Vector3d up = body.ToWorld.Y;
            Vector3d side = (lateral + pole * .42);
            side /= side.Length;

            var early = View(before, side, 850);
            var close = View(epoch, side, 850);
            var overhead = View(epoch.AddSeconds(850), (pole + lateral * .55) / Math.Sqrt(1 + .55 * .55), 530);
            ScenarioFile.Save(path, new([close, early], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || scene.Renderer(0).MissionParticleResult(id) is not { Count: 2001, TexturedEmitters: 1 } ||
                scene.Renderer(1).MissionParticleResult(id) is not null)
                throw new InvalidOperationException("Tvashtar must draw 2001 textured births in the active view and none before emission.");
            ScenarioFile.Save(path, new([close, overhead], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
                session[0] != close || session[1] != overhead)
                throw new InvalidOperationException("Independent Tvashtar views did not survive add-on reload.");
            var first = scene.Renderer(0).MissionParticleResult(id);
            var second = scene.Renderer(1).MissionParticleResult(id);
            if (first is not { Count: 2001, TexturedEmitters: 1, FirstVertex: not null } ||
                second is not { Count: >= 1999 and <= 2001, TexturedEmitters: 1, FirstVertex: not null } ||
                first.Value.FirstVertex == second.Value.FirstVertex)
                throw new InvalidOperationException("The two cameras must have independent textured plume geometry.");

            using var present = await Screenshot();
            if (present.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not save the Tvashtar GPU capture.");
            System.Environment.SetEnvironmentVariable(variable, absent);
            if (!await scene.ReloadAddOns() || scene.Renderer(0).MissionParticleResult(id) is not null ||
                session[0] != close || session[1] != overhead)
                throw new InvalidOperationException("Removing the particle catalogue must preserve both camera poses.");
            using var empty = await Screenshot();
            int changed = 0;
            for (int y = 100; y < present.GetHeight() - 70; y += 2)
            for (int x = 450; x < present.GetWidth() - 15; x += 2)
            {
                Color added = present.GetPixel(x, y), original = empty.GetPixel(x, y);
                if (added.B > original.B + .02f)
                    changed++;
            }
            if (changed < 100)
                throw new InvalidOperationException($"Tvashtar changed only {changed} sampled blue pixels.");
            GD.Print($"Tvashtar: {first.Value.Count}/{second.Value.Count} textured births, {changed} brighter GPU sample pixels, deterministic reload.");

            ObserverView View(JulianInstant at, Vector3d outward, double range)
            {
                var pose = new NavigationCamera(state.PositionKilometers, range, 0, 0, null, false)
                    { Placement = new NavigationPlacement(outward * range, outward * -1, up) };
                // Track the same Io site in the second active epoch without enabling a target lock.
                if (session.World.Graph.Evaluate(at).TryState(id, out var site))
                    pose = pose with { AnchorKilometers = site.PositionKilometers };
                return new(at, CameraMode.Solar, null, false) { Navigation = pose };
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, prior);
        }

        if (!await scene.ReloadAddOns())
            throw new InvalidOperationException("Could not restore the mission catalogues after the particle probe.");

        async Task<Image> Screenshot()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return GetViewport().GetTexture().GetImage();
        }
    }
}
