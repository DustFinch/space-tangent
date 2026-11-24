// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

// Process memory is evidence, not a timing/GC-dependent pass threshold.
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
                ProbeHost.Require(await scene.ReloadAddOns(), "The repeated real-data reload failed.");
                var actual = session.CaptureScenario(0);
                ProbeHost.Require(expected.Views.SequenceEqual(actual.Views) && expected.DeltaT == actual.DeltaT &&
                    expected.Synchronized == actual.Synchronized && expected.ClockOwner == actual.ClockOwner &&
                    expected.Layout == actual.Layout && expected.ActiveView == actual.ActiveView &&
                    identities.SequenceEqual(Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity)),
                    "Reload changed cameras, epochs, callback identities or delta-T.");
                await Frames();
                Capture(cycle);
            }
            RemoveChild(scene);
            scene.Free();
            scene = null;
            await Frames();
            Capture(25);
            File.WriteAllText(output, JsonSerializer.Serialize(marks, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print("SpaceTangent resource lifecycle probe passed: 24 split/close/reload cycles and scene shutdown");
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

    private async Task Frames()
    {
        for (int frame = 0; frame < 4; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }
}
