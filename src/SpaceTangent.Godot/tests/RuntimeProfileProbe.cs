// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Diagnostics;
using System.Text.Json;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

/// <summary>Frame wall time includes a framebuffer readback; this is not a GPU timestamp measurement.</summary>
public partial class RuntimeProfileProbe : Node
{
    public override async void _Ready()
    {
        int result = 1;
        try
        {
            FrameSampleChecks.Run();
            string output = System.Environment.GetEnvironmentVariable("SPACETANGENT_PROFILE_OUTPUT")
                ?? throw new InvalidOperationException("Set SPACETANGENT_PROFILE_OUTPUT.");
            Directory.CreateDirectory(output);
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            var timer = Stopwatch.StartNew();
            while (!scene.InitializationFinished)
            {
                if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("Startup profile timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            var session = scene.Session ?? throw new InvalidOperationException("Install DE440.");
            for (int frame = 0; scene.Renderer(0).HasPendingPaths || session.ViewCount > 1 && scene.Renderer(1).HasPendingPaths; frame++)
            {
                if (frame == 200) throw new TimeoutException("The paused scene did not complete its sampling jobs.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            await Frames("jason-full-moon");
            if (session[0].Navigation?.Target is { } trailTarget && session.World.Satellites.FirstOrDefault(item => item.Id == trailTarget) is { Display: { } display } track)
            {
                double duration = display.OrbitDurationSeconds ?? track.OrbitPeriodSeconds;
                var samples = scene.Renderer(0).MissionTrailSamples;
                if (samples.Count != 65) throw new InvalidOperationException("The complete trail must contain 65 samples.");
                for (int index = 0; index < samples.Count; index++)
                {
                    var at = session[0].Epoch.AddSeconds(-duration + (duration + display.OrbitLeadSeconds) * index / 64d);
                    var point = session.World.Graph.Evaluate(at, session.DeltaT);
                    Vector3d? expected = point.TryStateCenter(trailTarget, out var center) && point.TryState(trailTarget, out var body) && point.TryState(center, out var parent)
                        ? (body - parent).PositionKilometers : null;
                    if (expected.HasValue != samples[index].Offset.HasValue || expected is { } value && (value - samples[index].Offset!.Value).Length > 1e-6)
                        throw new InvalidOperationException("A scheduled Jason-1 sample differs from the full graph.");
                }
                GD.Print("Scheduled trail passed: all 65 real Jason-1 samples agree with full snapshots.");
            }
            if (!await scene.ReloadAddOns()) throw new InvalidOperationException("Reload failed.");
            var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
            var snapshot = session.World.Graph.Evaluate(epoch, session.DeltaT);
            var hole = BlackHoleCatalogue.Default.Entries.Single(item => item.Id == SimulationWorld.CygnusLensId);
            if (!snapshot.TryState(hole.Id, out var state) || !snapshot.TryState(SimulationWorld.CygnusCompanionId, out var companion))
                throw new InvalidOperationException("Install the compact object catalogue.");
            Vector3d transverse = (companion - state).PositionKilometers;
            transverse /= transverse.Length;
            Vector3d outward = hole.Direction * -1 + transverse * .65;
            outward /= outward.Length;
            Vector3d up = new Vector3d(0, 0, 1) - outward * outward.Z;
            var near = new ObserverView(epoch, CameraMode.Solar, hole.Id, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, 60 * hole.SchwarzschildRadiusKilometers, 0, 0, hole.Id, true)
                    { Placement = new NavigationPlacement(outward * (60 * hole.SchwarzschildRadiusKilometers), outward * -1, up / up.Length) },
                Sky = new(true, false, false, false) { DeepSky = true }
            };
            string scenario = Path.Combine(output, "kerr.stscene");
            ScenarioFile.Save(scenario, new([near], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(scenario)) throw new InvalidOperationException("Kerr scene failed.");
            await Frames("kerr-physical");
            ScenarioFile.Save(scenario, new([near with { Sky = near.Sky with { CinematicBlackHoles = true } }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(scenario)) throw new InvalidOperationException("Cinematic scene failed.");
            await Frames("kerr-cinematic");
            File.WriteAllText(Path.Combine(output, "profile.json"), JsonSerializer.Serialize(new
            {
                Engine = Engine.GetVersionInfo()["string"].AsString(),
                Renderer = RenderingServer.GetCurrentRenderingMethod(),
                Timings = RuntimeProfile.Snapshot()
            }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print("SpaceTangent runtime profile passed");
            result = 0;

            async Task Frames(string label)
            {
                for (int index = 0; index < 40; index++)
                {
                    var clock = Stopwatch.StartNew();
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using Image image = GetViewport().GetTexture().GetImage();
                    if (index >= 10) RuntimeProfile.Record("frame-readback." + label, clock.Elapsed.TotalMilliseconds);
                    if (index == 39) image.SavePng(Path.Combine(output, label + ".png"));
                }
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally { GetTree().Quit(result); }
    }
}
