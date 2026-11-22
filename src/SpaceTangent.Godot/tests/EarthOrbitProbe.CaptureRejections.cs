// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseCaptureRejections(SolarSystemScene scene, string directory)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        const string variable = "SPACETANGENT_CAPTURE_OUTPUT";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        System.Environment.SetEnvironmentVariable(variable, directory);
        var status = (Label)scene.FindChild("WorkspaceStatus", true, false);
        string script = Path.Combine(directory, "capture.lua");
        try
        {
            if (DisplayServer.GetName() == "headless")
            {
                if (scene.QueueScreenshot() || scene.StartRecording())
                    throw new InvalidOperationException("Headless capture APIs must refuse before opening output files.");
                File.WriteAllText(script, "st.capture('not-rendered') st.wait(5)");
                var running = scene.RunScript(script) ?? throw new InvalidOperationException("Open the headless capture script.");
                running.Step(0);
                if (running.Error is not null || !status.Text.Contains("rendered viewport", StringComparison.Ordinal) || Directory.GetFiles(directory, "*.png").Length != 0)
                    throw new InvalidOperationException("A script capture on a headless viewport must report unavailability and produce no image.");
                Escape();
            }
            else
            {
                if (scene.Session!.ViewCount == 1) scene.ToggleSplit();
                var otherLayers = scene.Session[1].Sky;
                var orbits = scene.FindChildren("*", "CheckButton", true, false).OfType<CheckButton>().Single(button => button.Text == "Planet orbits");
                bool original = orbits.ButtonPressed;
                orbits.ButtonPressed = !original;
                if (scene.Session[0].Sky.HideOrbits != original || scene.Session[1].Sky != otherLayers)
                    throw new InvalidOperationException("The orbit checkbox must affect only the active observer's layer.");
                orbits.ButtonPressed = original;
                var settings = (Button)scene.FindChild("SettingsTab", true, false);
                settings.EmitSignal(Button.SignalName.Pressed);
                if (!settings.ButtonPressed) throw new InvalidOperationException("The settings tab must open before testing Escape.");
                Escape();
                if (settings.ButtonPressed) throw new InvalidOperationException("Escape must close the workspace page when no script or replay owns it.");
                using (var capture = new InputEventKey { Keycode = Key.F10, Pressed = true }) scene._UnhandledKeyInput(capture);
                await Wait(() => Directory.GetFiles(directory, "*.png").Length == 1);
                using (var echo = new InputEventKey { Keycode = Key.F10, Pressed = true, Echo = true }) scene._UnhandledKeyInput(echo);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                if (Directory.GetFiles(directory, "*.png").Length != 1)
                    throw new InvalidOperationException("A repeated key echo must not queue a second screenshot.");
                string blocked = Path.Combine(directory, "blocked.png"); Directory.CreateDirectory(blocked);
                File.WriteAllText(script, "st.capture('blocked.png') st.wait(5)");
                var running = scene.RunScript(script) ?? throw new InvalidOperationException("Open the obstructed capture script.");
                running.Step(0);
                await Wait(() => status.Text.StartsWith("Could not save script screenshot.", StringComparison.Ordinal));
                if (!Directory.Exists(blocked)) throw new InvalidOperationException("The failed script capture must preserve the obstructing directory.");
                Escape();
            }
            string empty = Path.Combine(directory, "empty.stframes");
            using (var stream = File.Create(empty))
            using (var writer = new ObserverFrameSequence.Writer(stream, 30)) writer.Complete();
            if (scene.StartReplay(empty) || !status.Text.Contains("replay is empty", StringComparison.Ordinal))
                throw new InvalidOperationException("A valid recording envelope with no observer frames must be refused explicitly.");
            string replay = Path.Combine(directory, "cancel.stframes");
            using (var stream = File.Create(replay))
            using (var writer = new ObserverFrameSequence.Writer(stream, 30))
            {
                writer.Append(scene.Session!.CaptureScenario(0)); writer.Append(scene.Session.CaptureScenario(0)); writer.Complete();
            }
            if (!scene.StartReplay(replay)) throw new InvalidOperationException("The two-frame replay must start before Escape cancellation.");
            Escape();
            if (scene.Replaying) throw new InvalidOperationException("Escape must release an active replay immediately.");
            using var unlocked = new FileStream(replay, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.None);
            GD.Print("Capture rejections: headless availability or rendered shortcuts, queue failure, empty replay and Escape ownership passed.");
        }
        finally { System.Environment.SetEnvironmentVariable(variable, previous); }

        void Escape()
        {
            using var key = new InputEventKey { Keycode = Key.Escape, Pressed = true };
            scene._UnhandledKeyInput(key);
        }
        async Task Wait(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("Capture refusal timed out: " + status.Text);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
    }
}
