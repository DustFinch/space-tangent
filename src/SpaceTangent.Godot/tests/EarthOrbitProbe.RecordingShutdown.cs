// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseRecordingShutdown(SolarSystemScene original, string directory)
    {
        string restore = Path.Combine(directory, "before-recording-shutdown.stscene");
        if (!original.SaveScenario(restore)) throw new IOException("Preserve observers before recorder shutdown acceptance.");
        const string variable = "SPACETANGENT_CAPTURE_OUTPUT";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        try
        {
            foreach (bool renderFrames in new[] { true, false })
            {
                string output = Path.Combine(directory, "shutdown-" + Guid.NewGuid().ToString("N"));
                System.Environment.SetEnvironmentVariable(variable, output);
                int before = Engine.MaxFps;
                var candidate = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
                AddChild(candidate);
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!candidate.InitializationFinished)
                    {
                        if (clock.Elapsed.TotalSeconds > 30) throw new TimeoutException("Recorder shutdown scene startup timed out.");
                        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    }
                    if (!candidate.LoadScenario(restore) || !candidate.StartRecording())
                        throw new InvalidOperationException("The shutdown witness must start a real rendered recorder.");
                    if (renderFrames)
                    {
                        clock.Restart();
                        while (candidate.RecordedFrames < 2)
                        {
                            if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("The shutdown witness did not encode two frames.");
                            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                        }
                    }
                    RemoveChild(candidate);
                    if (Engine.MaxFps != before) throw new InvalidOperationException("Closing a recording scene must restore the global frame rate.");
                    string[] videos = Directory.GetFiles(output, "*.avi"), recordings = Directory.GetFiles(output, "*.stframes");
                    if (renderFrames)
                    {
                        if (videos.Length != 1 || recordings.Length != 1)
                            throw new InvalidOperationException("Closing a scene must finish and publish its existing recorded frames.");
                        using var stream = new FileStream(recordings[0], FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
                        using var sequence = new ObserverFrameSequence.Reader(stream);
                        int count = 0; while (sequence.ReadNext() is not null) count++;
                        if (count < 2 || count != sequence.FrameCount)
                            throw new InvalidOperationException("A shutdown recording must retain all frames and a valid completion header.");
                    }
                    else if (videos.Length != 0 || recordings.Length != 0)
                        throw new InvalidOperationException("Closing before the first draw must not publish an empty movie.");
                    if (Directory.GetFiles(output, "*.partial").Length != 0)
                        throw new InvalidOperationException("Recorder shutdown must remove unfinished temporary outputs.");
                }
                finally
                {
                    if (candidate.IsInsideTree()) RemoveChild(candidate);
                    candidate.QueueFree();
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
            }
            GD.Print("Recording shutdown GPU: active-scene close finishes valid frames, empty close discards partial outputs, source handles and frame rate recover.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
            if (!original.LoadScenario(restore)) throw new InvalidOperationException("Restore the original observers after recording shutdown.");
        }
    }
}
