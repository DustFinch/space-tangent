// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseStartupRecovery(string directory)
    {
        string malformed = Path.Combine(directory, "malformed-input.json"); File.WriteAllText(malformed, "{broken");
        string missing = Path.Combine(directory, "missing-input.bin");
        string surface = Path.Combine(directory, "broken-surface"); Directory.CreateDirectory(surface);
        File.WriteAllBytes(Path.Combine(surface, "earth-clouds.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(surface, "moon.jpg"), [1, 2, 3]);
        foreach (var (variable, value, diagnostic, hasSession) in new[]
        {
            ("SPACETANGENT_DE440", missing, "Set SPACETANGENT_DE440", false),
            ("SPACETANGENT_VSOP87", missing, "historical planetary series file is missing", false),
            ("SPACETANGENT_ADDONS_SETTINGS_PATH", malformed, "Could not read local add-on settings.", true),
            ("SPACETANGENT_BOOKMARKS_PATH", malformed, "Could not read bookmarks.", true),
            ("SPACETANGENT_MISSION_CATALOG", malformed, "Could not load add-ons.", true),
            ("SPACETANGENT_SURFACE_TEXTURES", surface, "Could not decode the planetary texture", true)
        })
        {
            string? original = System.Environment.GetEnvironmentVariable(variable);
            SolarSystemScene? candidate = null;
            try
            {
                System.Environment.SetEnvironmentVariable(variable, value);
                candidate = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
                AddChild(candidate);
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!candidate.InitializationFinished)
                {
                    if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("Recovery startup did not complete.");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                var status = (Label)candidate.FindChild("WorkspaceStatus", recursive: true, owned: false);
                if ((candidate.Session is not null) != hasSession || !status.Text.Contains(diagnostic, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Incorrect recovery for {variable}: {status.Text}");
                if (hasSession && !candidate.Session![0].Navigation!.Value.IsValid())
                    throw new InvalidOperationException("A recoverable settings error must retain a usable camera.");
                GD.Print($"Startup recovery: {variable}, session={hasSession}, diagnostic={diagnostic}");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(variable, original);
                if (candidate is not null) { RemoveChild(candidate); candidate.QueueFree(); }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
    }
}
