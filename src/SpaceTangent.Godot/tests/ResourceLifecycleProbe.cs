// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

// Native identity and warmed engine counts are assertions. Process memory is
// evidence only; explicit finalizer drains here are not performance measurements.
public partial class ResourceLifecycleProbe : Node
{
    private sealed record Mark(int Cycle, long ManagedBytes, long PrivateBytes, double Objects, double Resources, double Nodes);

    public override async void _Ready()
    {
        SolarSystemScene? scene = null;
        int exit = 1;
        var marks = new List<Mark>();
        try
        {
            string output = System.Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_CAPTURE")
                ?? throw new InvalidOperationException("An isolated evidence path is required.");
            await WarmUp();
            await DrainFinalizers();
            Capture(-1);
            Mark baseline = marks[^1];
            GetTree().Root.Size = new(1600, 900);
            scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            await ProbeHost.Until(this, () => scene.InitializationFinished, 60, "Lifecycle startup timed out.");
            var session = scene.Session ?? throw new InvalidOperationException("Real simulation inputs are required.");
            ProbeHost.Require(session.World.Satellites.Any(body => body.Id == new ObjectId("norad/26997")), "Jason-1 is required.");
            session.SetPlaying(0, false);
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            scene.SetMode(0, CameraMode.Satellites);
            if (session.ViewCount == 1) scene.ToggleSplit();
            scene.ShowFullMoon(1);
            session.SetPlaying(1, false);
            session.Synchronize(false, 0);
            session.Step(1, 3600);
            ProbeHost.Require(scene.PublishedAddons?.Mission is not null, "The real Cassini mission is required for generation changes.");
            ulong scienceId = scene.PublishedAddons!.Satellites.First(body => body.Track.Id == new ObjectId("norad/26997")).Mesh.GetInstanceId();
            var missionIds = new HashSet<ulong> { scene.PublishedAddons.Mission!.Mesh.GetInstanceId() };
            await Frames();
            Capture(0);
            for (int cycle = 1; cycle <= 24; cycle++)
            {
                var first = session[0];
                int identity = session.ViewIdentity(0);
                scene.ToggleSplit();
                await Frames();
                ProbeHost.Require(session.ViewCount == 1 && session[0] == first && session.ViewIdentity(0) == identity,
                    "Closing the second view changed the first camera, clock or identity.");
                scene.ToggleSplit();
                scene.ShowFullMoon(1);
                session.SetPlaying(1, false);
                session.Synchronize(false, 0);
                session.Step(1, 3600);
                await Frames();
                var expected = session.CaptureScenario(0);
                int[] identities = Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity).ToArray();
                var previous = scene.PublishedAddons;
                ProbeHost.Require(await scene.ReloadAddOns() && ReferenceEquals(previous, scene.PublishedAddons), "The no-op reload must retain its generation.");
                var actual = session.CaptureScenario(0);
                ProbeHost.Require(expected.Views.SequenceEqual(actual.Views) && expected.DeltaT == actual.DeltaT &&
                    expected.Synchronized == actual.Synchronized && expected.ClockOwner == actual.ClockOwner &&
                    expected.Layout == actual.Layout && expected.ActiveView == actual.ActiveView &&
                    identities.SequenceEqual(Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity)),
                    "Reload changed cameras, epochs, callback identities or delta-T.");
                await Frames();
                Capture(cycle);
            }
            for (int cycle = 1; cycle <= 24; cycle++)
            {
                var previous = scene.PublishedAddons!;
                if (previous.Mission is not null)
                {
                    var epoch = SimulationSession.MissionPreview;
                    var observer = new ObserverView(epoch, CameraMode.Mission, previous.Mission.Id, false);
                    ProbeHost.Require(scene.Renderer(0).Draw(observer, session.World.Graph.Evaluate(epoch, session.DeltaT), session.DeltaT) &&
                        scene.Renderer(0).MissionMesh.Mesh?.GetInstanceId() == previous.Mission.Mesh.GetInstanceId(),
                        "The retiring mission mesh must first be borrowed by a rendered view.");
                }
                ulong? oldMesh = previous.Mission?.Mesh.GetInstanceId();
                var expected = session.CaptureScenario(0);
                var requested = scene.AppliedAddonSettings.SetDefault(SpaceTangent.Application.Addons.AddonSourceKind.Mission, previous.Mission is null);
                ProbeHost.Require(await scene.ReloadAddOns(requested), "The changed real mission must publish.");
                var current = scene.PublishedAddons!;
                ProbeHost.Require(!ReferenceEquals(previous, current) && previous.IsDisposed && scene.RetiringAddons is null,
                    "Every changed cycle must publish and retire a real generation.");
                if (oldMesh is { } retired)
                    ProbeHost.Require(!GodotObject.IsInstanceIdValid(retired), "The obsolete mission mesh must be deleted natively.");
                if (current.Mission is { } mission)
                    ProbeHost.Require(missionIds.Add(mission.Mesh.GetInstanceId()), "Enabling the mission must create a fresh native mesh.");
                ProbeHost.Require(current.Satellites.First(body => body.Track.Id == new ObjectId("norad/26997")).Mesh.GetInstanceId() == scienceId &&
                    GodotObject.IsInstanceIdValid(scienceId), "The unchanged Jason-1 resource must remain shared.");
                var actual = session.CaptureScenario(0);
                ProbeHost.Require(expected.Views.SequenceEqual(actual.Views) && expected.DeltaT == actual.DeltaT && expected.Layout == actual.Layout &&
                    expected.Synchronized == actual.Synchronized && expected.ClockOwner == actual.ClockOwner,
                    "Mission publication must preserve both cameras, epochs and delta-T.");
                ProbeHost.Require(await scene.ReloadAddOns() && ReferenceEquals(current, scene.PublishedAddons), "No-op after a changed generation must reuse it.");
                await Frames();
                Capture(24 + cycle);
            }
            RemoveChild(scene);
            scene.Free();
            scene = null;
            await DrainFinalizers();
            Capture(49);
            ProbeHost.Require(missionIds.All(id => !GodotObject.IsInstanceIdValid(id)) && !GodotObject.IsInstanceIdValid(scienceId),
                "Scene shutdown must delete all created mission meshes and the shared science mesh.");
            Mark final = marks[^1];
            File.WriteAllText(output, JsonSerializer.Serialize(marks, new JsonSerializerOptions { WriteIndented = true }));
            ProbeHost.Require(final.Nodes == baseline.Nodes && final.Resources <= baseline.Resources + 2,
                $"Native objects did not return to the warmed baseline: nodes {baseline.Nodes}->{final.Nodes}, resources {baseline.Resources}->{final.Resources} (allow 2 cached resources).");
            GD.Print("SpaceTangent resource lifecycle probe passed: 24 split/no-op cycles, 24 changed generations, native deletion and warmed baseline");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally { ProbeHost.FreeScene(this, scene); ProbeHost.Complete(this, exit); }

        void Capture(int cycle)
        {
            using var process = Process.GetCurrentProcess();
            marks.Add(new(cycle, GC.GetTotalMemory(false), process.PrivateMemorySize64,
                Performance.GetMonitor(Performance.Monitor.ObjectCount),
                Performance.GetMonitor(Performance.Monitor.ObjectResourceCount),
                Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)));
        }
    }

    private async Task WarmUp()
    {
        SolarSystemScene? warm = null;
        try
        {
            warm = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>(); AddChild(warm);
            await ProbeHost.Until(this, () => warm.InitializationFinished, 60, "Warm-up startup timed out.");
            ProbeHost.Require(warm.Session is not null && warm.PublishedAddons?.Mission is not null, "Warm-up requires the real mission.");
            warm.Session!.SetPlaying(0, false); warm.SetMode(0, CameraMode.Satellites);
            if (warm.Session.ViewCount == 1) warm.ToggleSplit();
            warm.ShowFullMoon(1); warm.Session.Synchronize(false, 0);
            foreach (bool enabled in new[] { false, true })
                ProbeHost.Require(await warm.ReloadAddOns(warm.AppliedAddonSettings.SetDefault(SpaceTangent.Application.Addons.AddonSourceKind.Mission, enabled)), "Warm up both publication states.");
            await Frames();
        }
        finally { ProbeHost.FreeScene(this, warm); warm = null; }
    }

    private async Task DrainFinalizers()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            await Frames();
        }
    }

    private async Task Frames()
    {
        for (int frame = 0; frame < 4; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
