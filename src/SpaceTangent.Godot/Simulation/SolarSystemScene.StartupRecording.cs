// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private string[] _startupArguments = [];

    private bool InspectStartupArchive()
    {
        _startupArguments = OS.GetCmdlineUserArgs();
        if (!_startupArguments.Contains("--inspect-legacy-recording", StringComparer.Ordinal))
            return false;
        try
        {
            string path = RequiredArgument("--inspect-legacy-recording");
            using var file = File.OpenRead(path);
            GD.Print(JsonSerializer.Serialize(LegacyFrameInspection.Inspect(file)));
            GetTree().Quit(0);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.Print("Could not inspect legacy frame recording: " + error.Message);
            GetTree().Quit(1);
        }
        return true;
    }

    private async Task RunStartupRecording()
    {
        try
        {
            if (_startupArguments.Contains("--startup-scenario", StringComparer.Ordinal) &&
                !LoadScenario(RequiredArgument("--startup-scenario")))
                throw new InvalidDataException(_status.Text);
            if (_startupArguments.Contains("--startup-legacy-settings", StringComparer.Ordinal))
                ImportStartupSettings(RequiredArgument("--startup-legacy-settings"));
            if (_startupArguments.Contains("--startup-save-scenario", StringComparer.Ordinal) &&
                !SaveScenario(RequiredArgument("--startup-save-scenario")))
                throw new InvalidDataException(_status.Text);
            if (_startupArguments.Contains("--startup-replay", StringComparer.Ordinal) &&
                !StartReplay(RequiredArgument("--startup-replay")))
                throw new InvalidDataException(_status.Text);
            if (_startupArguments.Contains("--startup-screenshot", StringComparer.Ordinal))
            {
                while (Replaying)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!QueueScreenshot(RequiredArgument("--startup-screenshot")))
                    throw new InvalidDataException(_status.Text);
            }
            if (_startupArguments.Contains("--startup-movie", StringComparer.Ordinal))
            {
                if (!double.TryParse(RequiredArgument("--startup-movie"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double duration) || duration is < .1 or > 600)
                    throw new InvalidDataException("Movie duration must be .1–600 seconds.");
                if (!StartRecording((int)Math.Round(WindowRecording.FramesPerSecond * duration)))
                    throw new InvalidDataException(_status.Text);
            }
            if (!_startupArguments.Contains("--startup-exit", StringComparer.Ordinal))
                return;
            if (MovieActive || Replaying || _screenshotPath is not null)
            {
                // Movie finalization and replay are driven by the normal frame callbacks.
                while (MovieActive || _finishingMovie || Replaying || _screenshotPath is not null)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            GetTree().Quit(_captureFailed ? 1 : 0);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = error.Message;
            GD.Print("Startup recording: " + error.Message);
            GetTree().Quit(1);
        }
    }

    private void ImportStartupSettings(string path)
    {
        var session = Session!;
        var profile = LegacyObserverProfiles.Read(path, session[_active], session.DeltaT);
        var current = session.CaptureScenario(_active);
        ObserverView[] views = current.Views.ToArray();
        views[_active] = profile.Observer;
        ApplyScenario(current with { Views = Array.AsReadOnly(views), DeltaT = profile.DeltaT });
        GD.Print("Legacy observer profile applied to the active view.");
    }

    private string RequiredArgument(string name)
    {
        int at = Array.IndexOf(_startupArguments, name);
        if (at < 0 || at + 1 >= _startupArguments.Length || _startupArguments[at + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{name} needs a value.");
        return _startupArguments[at + 1];
    }
}
