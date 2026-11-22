// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseRecording(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_RECORDING_PROBE") != "1" ||
            DisplayServer.GetName() == "headless")
            return;
        const string setting = "SPACETANGENT_CAPTURE_OUTPUT";
        string? previous = System.Environment.GetEnvironmentVariable(setting);
        string output = Path.Combine(directory, "Съёмка двух видов", Guid.NewGuid().ToString("N"));
        System.Environment.SetEnvironmentVariable(setting, output);
        try
        {
            var session = scene.Session!;
            var jason = new ObjectId("norad/26997");
            var instant = JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb);
            if (!session.World.Graph.Evaluate(instant).TryState(jason, out var state))
                throw new InvalidOperationException("The original Jason-1 state is unavailable for recording.");
            var left = new ObserverView(instant, CameraMode.Solar, jason, false)
            {
                Navigation = new(state.PositionKilometers, .08, .46, .31, jason, true),
                PlaybackRate = 30
            };
            var right = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
                { EarthObserver = EarthObserverCamera.Moscow };
            string scenario = Path.Combine(directory, "capture-jason-moon.stscene");
            ScenarioFile.Save(scenario, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(scenario))
                throw new InvalidOperationException("Could not open the two-view recording fixture.");
            string screenshot = Path.Combine(output, "jason-moon.png");
            if (!scene.QueueScreenshot(screenshot))
                throw new InvalidOperationException("Could not queue a screenshot.");
            await WaitFor(() => File.Exists(screenshot), "Saving the rendered PNG");
            using (Image image = Image.LoadFromFile(screenshot))
                if (image.IsEmpty() || image.GetWidth() < 500 || image.GetHeight() < 300)
                    throw new InvalidDataException("The screenshot is not a full workspace image.");

            session.SetPlaying(0, true);
            if (!scene.StartRecording())
                throw new InvalidOperationException("Could not start the fixed-step movie.");
            await WaitFor(() => scene.RecordedFrames >= 8, "Recording eight real frames");
            var recording = await scene.StopRecording() ?? throw new InvalidOperationException("The movie was not published.");
            using var file = File.OpenRead(recording.ObserverFrames);
            using var replay = new ObserverFrameSequence.Reader(file);
            SimulationScenario? previousFrame = null, last = null;
            while (replay.ReadNext() is { } frame)
            {
                if (frame.Views[1] != right || frame.DeltaT != DeltaTModel.MorrisonStephenson2004 ||
                    frame.Views[0].Selection != jason ||
                    previousFrame is not null && frame.Views[0].Epoch.DifferenceNanoseconds(previousFrame.Views[0].Epoch) !=
                        JulianInstant.NanosecondsPerSecond)
                    throw new InvalidOperationException("A recorded observer changed or the simulated step was not one second.");
                previousFrame = last = frame;
            }
            if (last is null || replay.FramesPerSecond != 30 || replay.FrameCount != recording.Frames ||
                !File.Exists(recording.Video) || !scene.StartReplay(recording.ObserverFrames))
                throw new InvalidOperationException("Could not play the exact recorded observer sequence.");
            await WaitFor(() => !scene.Replaying, "Replaying the two camera views");
            if (session[0].Epoch != last.Views[0].Epoch || session[0].Navigation != last.Views[0].Navigation ||
                session[1] != right || session.DeltaT != last.DeltaT)
                throw new InvalidOperationException("The replay must finish at the last recorded cameras and epoch.");
            GD.Print($"Recording GPU: PNG and MJPEG AVI at 30 fps, {recording.Frames} frames, exact observer replay.");
            GD.Print("Recording output: " + recording.Video);
            await ExerciseRecordingBoundaries(scene, output, scenario, recording.ObserverFrames);
        }
        finally { System.Environment.SetEnvironmentVariable(setting, previous); }

        async Task WaitFor(Func<bool> condition, string step)
        {
            var time = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (time.Elapsed.TotalSeconds > 15)
                    throw new TimeoutException($"{step} timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
    }

    private async Task ExerciseRecordingBoundaries(SolarSystemScene scene, string output, string scenario, string replay)
    {
        var status = (Label)scene.FindChild("WorkspaceStatus", true, false);
        string script = Path.Combine(output, "capture-contract.lua");
        foreach (string invalid in new[] { "CON", "LPT9.png", "trailing.", "trailing ", ".", "..", "folder/name" })
        {
            File.WriteAllText(script, $"st.capture('{invalid}')");
            var running = scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the filename boundary script.");
            running.Step(0);
            Require(running.Error?.Contains("plain filename", StringComparison.Ordinal) == true,
                "Reserved device names and ambiguous capture paths must be refused.");
        }
        for (int index = 0; index < 33; index++)
        {
            File.WriteAllText(script, $"st.capture('queued-{index}')");
            var queued = scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the queue boundary script.");
            queued.Step(0);
            Require(index == 32 ? queued.Error?.Contains("Too many script screenshots", StringComparison.Ordinal) == true : queued.Error is null,
                "Separate scripts must share the 32-image host queue until a rendered frame. Actual: " + queued.Error);
        }
        await Wait(() => Directory.GetFiles(output, "queued-*.png").Length == 32);
        Require(!File.Exists(Path.Combine(output, "queued-32.png")), "The refused screenshot must not be written later.");

        Require(scene.StartRecording(2), "The bounded movie must start.");
        Require(!scene.StartRecording() && !scene.StartReplay(replay), "A live movie must reject a second movie and replay.");
        await Wait(() => status.Text.StartsWith("Saved 2 frames", StringComparison.Ordinal));
        Require(await scene.StopRecording() is null, "The frame budget must close the active recorder.");
        int oldMaximum = Engine.MaxFps;
        using (var press = new InputEventKey { Keycode = Key.F10, ShiftPressed = true, Pressed = true })
            scene._UnhandledKeyInput(press);
        await Wait(() => scene.RecordedFrames >= 1);
        using (var press = new InputEventKey { Keycode = Key.F10, ShiftPressed = true, Pressed = true })
            scene._UnhandledKeyInput(press);
        await Wait(() => status.Text.StartsWith("Saved ", StringComparison.Ordinal));
        Require(Engine.MaxFps == oldMaximum, "The recording shortcut must restore the previous frame limit.");

        string blocked = Path.Combine(output, "blocked-output"); File.WriteAllText(blocked, "keep");
        string? previous = System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT");
        try
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT", blocked);
            Require(!scene.StartRecording() && status.Text.StartsWith("Could not start recording.", StringComparison.Ordinal),
                "An unwritable movie directory must report failure without starting a worker.");
            Require(scene.QueueScreenshot(Path.Combine(blocked, "refused.png")), "The screenshot path can be queued before the filesystem failure.");
            await Wait(() => status.Text.StartsWith("Could not save screenshot.", StringComparison.Ordinal));
            Require(File.ReadAllText(blocked) == "keep", "A failed capture must preserve the obstructing file.");
        }
        finally { System.Environment.SetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT", previous); }
        Require(scene.LoadScenario(scenario) && scene.StartReplay(replay), "Replay must recover after failed capture output.");
        Require(!scene.StartRecording(), "Recording must not replace an active replay.");
        await Wait(() => !scene.Replaying);
        string corrupt = Path.Combine(output, "corrupt-second-frame.stframes");
        using (var recordingFile = File.Create(corrupt))
        using (var writer = new ObserverFrameSequence.Writer(recordingFile, 30))
        {
            writer.Append(scene.Session!.CaptureScenario(0));
            writer.Append(scene.Session.CaptureScenario(0));
            writer.Complete();
        }
        byte[] damaged = File.ReadAllBytes(corrupt); damaged[^1] ^= 1; File.WriteAllBytes(corrupt, damaged);
        scene.SetProcess(false);
        try
        {
            Require(scene.StartReplay(corrupt), "A replay must open its first valid frame before reaching later corruption.");
            scene._Process(1d / 30);
            Require(!scene.Replaying && status.Text.StartsWith("Could not advance observer replay.", StringComparison.Ordinal),
                "A later frame digest failure must stop playback and report a controlled error.");
        }
        finally {scene.SetProcess(true);}
        using (var unlocked = new FileStream(corrupt, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None))
            Require(unlocked.Length == damaged.Length, "A failed replay must close its source handle.");
        Require(scene.StartRecording(), "The empty movie must start before its first draw.");
        Require(await scene.StopRecording() is null && status.Text.StartsWith("Could not finish recording.", StringComparison.Ordinal),
            "Finishing a movie before any draw must fail cleanly and restore the recorder state.");
        GD.Print("Recording boundaries GPU: filenames, 32-image queue, exact two-frame limit, shortcuts, output refusal and replay recovery passed.");

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        async Task Wait(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Recording boundary timed out: " + status.Text);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
    }
}
