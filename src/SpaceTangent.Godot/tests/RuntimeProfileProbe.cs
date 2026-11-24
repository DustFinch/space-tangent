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
    private readonly record struct FrameAllocation(long ProcessAllocatedBytes, int Gen0, int Gen1, int Gen2);
    private readonly record struct MemoryMark(string Label, long ManagedBytes, long PrivateBytes, long WorkingSetBytes,
        long TotalAllocatedBytes, int Gen0, int Gen1, int Gen2);

    private static MemoryMark Memory(string label)
    {
        using var process = Process.GetCurrentProcess();
        return new(label, GC.GetTotalMemory(forceFullCollection: false), process.PrivateMemorySize64,
            process.WorkingSet64, GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    public override async void _Ready()
    {
        int result = 1;
        try
        {
            FrameSampleChecks.Run();
            string output = System.Environment.GetEnvironmentVariable("SPACETANGENT_PROFILE_OUTPUT")
                ?? throw new InvalidOperationException("Set SPACETANGENT_PROFILE_OUTPUT.");
            Directory.CreateDirectory(output);
            var allocations = new Dictionary<string, FrameAllocation[]>();
            var memory = new List<MemoryMark> { Memory("before-startup") };
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
            memory.Add(Memory("after-startup-frames"));
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
            await Frames("jason-forward-paths", 600);
            await Frames("jason-reverse-paths", -600);
            if (!await scene.ReloadAddOns()) throw new InvalidOperationException("Reload failed.");
            memory.Add(Memory("after-first-reload"));
            for (int cycle = 1; cycle <= 8; cycle++)
            {
                if (!await scene.ReloadAddOns()) throw new InvalidOperationException("Repeated reload failed.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                memory.Add(Memory("after-reload-" + cycle));
            }
            var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
            string workload = Path.Combine(output, "workload.stscene");
            var earth = new ObserverView(epoch, CameraMode.Solar, SolarSystemSample.EarthId, false);
            ScenarioFile.Save(workload, new([earth], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(workload)) throw new InvalidOperationException("Earth profile scene failed.");
            session.OpenSpaceView(0);
            scene.Seek(0, epoch);
            await Frames("earth-space");
            ScenarioFile.Save(workload, new([earth with { Mode = CameraMode.Surface,
                EarthObserver = EarthObserverCamera.GreenwichFullMoon }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(workload)) throw new InvalidOperationException("Surface profile scene failed.");
            await Frames("earth-surface");
            if (session.World.MissionId is not null)
            {
                scene.SetMode(0, CameraMode.Mission);
                await Frames("cassini-mission");
            }
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
            var logicalSize = scene.Renderer(0).GetViewport().GetVisibleRect().Size;
            var centerPixel = logicalSize / 2;
            var fullPointer = scene.Renderer(0).PointerDirection(centerPixel);
            var fullPick = scene.Renderer(0).PickObject(centerPixel);
            scene.LensRenderScale = .5f;
            scene.Seek(0, epoch);
            await Frames("kerr-physical-half");
            if (!scene.Renderer(0).CompactLensVisible || scene.Renderer(0).GetViewport().Scaling3DScale != .5f ||
                scene.Renderer(0).GetViewport().GetVisibleRect().Size != logicalSize ||
                scene.Renderer(0).PointerDirection(centerPixel) != fullPointer || scene.Renderer(0).PickObject(centerPixel) != fullPick)
                throw new InvalidOperationException("Lens scaling changed logical picking or projection coordinates.");
            using (Image full = Image.LoadFromFile(Path.Combine(output, "kerr-physical.png")))
            using (Image reduced = Image.LoadFromFile(Path.Combine(output, "kerr-physical-half.png")))
            {
                double difference = 0;
                int pixels = 0;
                for (int y = 112; y < full.GetHeight(); y += 2)
                for (int x = 354; x < full.GetWidth(); x += 2)
                {
                    Color a = full.GetPixel(x, y), b = reduced.GetPixel(x, y);
                    difference += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3;
                    pixels++;
                }
                if (difference / pixels > .02) throw new InvalidOperationException("Reduced lens resolution changed the physical image excessively.");
                GD.Print(FormattableString.Invariant($"Lens resolution check: mean absolute RGB difference {difference / pixels:F6}; logical picking preserved."));
            }
            scene.LensRenderScale = 1;
            ScenarioFile.Save(scenario, new([near with { Sky = near.Sky with { CinematicBlackHoles = true } }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(scenario)) throw new InvalidOperationException("Cinematic scene failed.");
            await Frames("kerr-cinematic");
            scene.LensRenderScale = .5f;
            scene.Seek(0, epoch);
            await Frames("kerr-cinematic-half");
            ScenarioFile.Save(scenario, new([near with { Navigation = null, Selection = SolarSystemSample.EarthId }], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(scenario) || scene.Renderer(0).GetViewport().Scaling3DScale != 1)
                throw new InvalidOperationException("Ordinary views must restore full 3D resolution.");
            File.WriteAllText(Path.Combine(output, "profile.json"), JsonSerializer.Serialize(new
            {
                Engine = Engine.GetVersionInfo()["string"].AsString(),
                Renderer = RenderingServer.GetCurrentRenderingMethod(),
                Timings = RuntimeProfile.Snapshot(),
                FrameAllocations = allocations,
                Memory = memory.Append(Memory("after-all-workloads")).ToArray(),
                MemoryScope = "Precise process-wide managed allocations and GC, including probe/readback work; heap snapshots do not force collection. Frame wall time includes readback."
            }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print("SpaceTangent runtime profile passed");
            result = 0;

            async Task Frames(string label, double stepSeconds = 0)
            {
                var measured = new FrameAllocation[30];
                for (int index = 0; index < 40; index++)
                {
                    long before = GC.GetTotalAllocatedBytes(precise: true);
                    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                    var clock = Stopwatch.StartNew();
                    if (stepSeconds != 0)
                        scene.Seek(0, session[0].Epoch.AddSeconds(stepSeconds));
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using Image image = GetViewport().GetTexture().GetImage();
                    if (index >= 10)
                    {
                        double elapsed = clock.Elapsed.TotalMilliseconds;
                        measured[index - 10] = new(GC.GetTotalAllocatedBytes(precise: true) - before,
                            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
                        RuntimeProfile.Record("frame-readback." + label, elapsed);
                    }
                    if (index == 39) image.SavePng(Path.Combine(output, label + ".png"));
                }
                allocations.Add(label, measured);
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally { GetTree().Quit(CoverageCapture.Complete(result)); }
    }
}
