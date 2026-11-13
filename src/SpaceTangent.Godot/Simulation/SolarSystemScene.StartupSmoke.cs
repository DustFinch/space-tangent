// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Globalization;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private async Task RunStartupSmoke()
    {
        try
        {
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SMOKE_PORTABLE_ADDONS") is { Length: > 0 } portableCount)
            {
                var sources = _localAddons.Sources.Where(source => source.Enabled).ToArray();
                if (!int.TryParse(portableCount, out int expectedCount) || sources.Length != expectedCount ||
                    sources.Any(source => !source.RelativeToData ||
                        _addOns?.Decoded.HasSource(source.Kind, AddonLoader.SourcePath(source, _localAddonFile)) != true))
                    throw new InvalidDataException("The restarted simulator did not load the expected portable add-on sources.");
                GD.Print($"SpaceTangent startup loaded {sources.Length} portable add-on sources.");
            }

            string? path = System.Environment.GetEnvironmentVariable("SPACETANGENT_SMOKE_SCENARIO");
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!LoadScenario(path))
                    throw new InvalidDataException(_status.Text);
                SimulationScenario expected = ScenarioFile.Load(path);
                SimulationScenario actual = Session!.CaptureScenario(_active);
                if (!actual.Views.SequenceEqual(expected.Views) || actual.DeltaT != expected.DeltaT ||
                    actual.ActiveView != expected.ActiveView || actual.ClockOwner != expected.ClockOwner ||
                    actual.Synchronized != expected.Synchronized || actual.SplitLayout != expected.SplitLayout ||
                    actual.Layout != expected.Layout)
                    throw new InvalidDataException("The restarted simulator did not restore the exact scenario.");
                GD.Print($"SpaceTangent startup restored {actual.Views.Count} views with ΔT {actual.DeltaT}.");
            }

            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SMOKE_SOAK_SECONDS") is { } soakSeconds)
                await RunStartupSoak(soakSeconds);

            string? capture = System.Environment.GetEnvironmentVariable("SPACETANGENT_SMOKE_CAPTURE");
            if (!string.IsNullOrWhiteSpace(capture))
            {
                if (DisplayServer.GetName() == "headless")
                    throw new InvalidOperationException("A rendering display driver is required to capture a frame.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                foreach (ViewControls controls in _views.Where(view => view.FieldOfView.Visible))
                {
                    SpinBox field = controls.FieldOfView;
                    string suffix = " " + field.Suffix;
                    LineEdit readout = field.GetLineEdit();
                    string displayed = readout.Text;
                    if (!displayed.EndsWith(suffix, StringComparison.Ordinal) ||
                        !double.TryParse(displayed.AsSpan(0, displayed.Length - suffix.Length),
                            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double degrees) ||
                        Math.Abs(degrees - field.Value) > .000001)
                        throw new InvalidDataException($"The lens shows '{displayed}' instead of {field.Value} degrees.");
                    float width = readout.GetThemeFont("font").GetStringSize(displayed,
                        fontSize: readout.GetThemeFontSize("font_size")).X;
                    if (width + readout.GetThemeStylebox("normal").GetMinimumSize().X > readout.Size.X)
                        throw new InvalidDataException($"The lens readout '{displayed}' is clipped.");
                    GD.Print($"SpaceTangent startup verified lens readout {displayed}.");
                }
                using Image image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(capture) != Error.Ok)
                    throw new IOException("Could not write the startup frame.");
                GD.Print($"SpaceTangent startup captured {image.GetWidth()}×{image.GetHeight()}.");
            }
            GetTree().Quit();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushError(error.Message);
            GetTree().Quit(1);
        }
    }

    private async Task RunStartupSoak(string durationText)
    {
        if (DisplayServer.GetName() == "headless" ||
            !int.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out int duration) ||
            duration is < 5 or > 3600)
            throw new InvalidDataException("The GPU soak needs a duration between 5 and 3600 seconds.");

        SimulationSession session = Session!;
        if (session.ViewCount != 2 || session.Synchronized || session[0].Epoch == session[1].Epoch ||
            session[0].Playing || session[1].Playing || Assets.Count == 0)
            throw new InvalidDataException("The GPU soak needs two paused views at distinct epochs and installed satellites.");

        var expected = session.CaptureScenario(_active);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        int interval = Math.Min(180, Math.Max(2, duration / 3));
        int reloads = 0;
        double next = interval;
        int firstFrame = Engine.GetFramesDrawn();
        int lastReloadFrame = firstFrame;
        GD.Print($"SpaceTangent soak started for {duration} s.");
        while (timer.Elapsed.TotalSeconds < duration)
        {
            await ToSignal(GetTree().CreateTimer(Math.Clamp(duration - timer.Elapsed.TotalSeconds, .001, 1)), SceneTreeTimer.SignalName.Timeout);
            if (timer.Elapsed.TotalSeconds < next)
                continue;

            int frame = Engine.GetFramesDrawn();
            if (frame <= lastReloadFrame)
                throw new InvalidDataException("The GPU soak stopped drawing frames.");
            if (!await ReloadAddOns())
                throw new InvalidDataException("Add-on reload failed during the GPU soak.");
            SimulationScenario actual = session.CaptureScenario(_active);
            if (!actual.Views.SequenceEqual(expected.Views) || actual.DeltaT != expected.DeltaT ||
                actual.ActiveView != expected.ActiveView || actual.ClockOwner != expected.ClockOwner ||
                actual.Synchronized != expected.Synchronized || actual.Layout != expected.Layout)
                throw new InvalidDataException("The GPU soak lost a camera, epoch or time setting after reload.");

            lastReloadFrame = frame;
            next += interval;
            process.Refresh();
            GD.Print(FormattableString.Invariant(
                $"SpaceTangent soak reload {++reloads}, {timer.Elapsed.TotalSeconds:F1} s, {frame - firstFrame} rendered frames, {process.PrivateMemorySize64} private bytes, {GC.GetTotalMemory(false)} managed bytes."));
        }
        GD.Print(FormattableString.Invariant(
            $"SpaceTangent soak completed {timer.Elapsed.TotalSeconds:F1} s, {reloads} reloads, {Engine.GetFramesDrawn() - firstFrame} rendered frames."));
    }
}
