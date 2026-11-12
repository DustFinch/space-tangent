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
        string output = Path.Combine(directory, "Съёмка двух видов");
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
}
