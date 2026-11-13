// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private static string StartupWorkspacePath()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://startup-workspace.stscene") : configured);
    }

    private void RestoreStartupWorkspace()
    {
        if (_startupArguments.Contains("--smoke-startup", StringComparer.Ordinal) ||
            _startupArguments.Contains("--startup-scenario", StringComparer.Ordinal) ||
            _startupArguments.Contains("--startup-default-settings", StringComparer.Ordinal))
            return;
        try
        {
            string path = StartupWorkspacePath();
            if (File.Exists(path) && LoadScenario(path))
                GD.Print("SpaceTangent restored the saved startup views.");
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not restore startup views. {error.Message}";
        }
    }

    internal bool SaveStartupWorkspace()
    {
        try
        {
            string path = StartupWorkspacePath();
            if (!SaveScenario(path))
                return false;
            _status.Text = "Current views saved for startup.";
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not save startup views. {error.Message}";
            return false;
        }
    }

    private void ClearStartupWorkspace()
    {
        try
        {
            File.Delete(StartupWorkspacePath());
            _status.Text = "Saved startup views cleared.";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not clear startup views. {error.Message}";
        }
    }
}
