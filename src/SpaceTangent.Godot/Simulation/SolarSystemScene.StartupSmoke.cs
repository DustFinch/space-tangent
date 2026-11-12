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
}
