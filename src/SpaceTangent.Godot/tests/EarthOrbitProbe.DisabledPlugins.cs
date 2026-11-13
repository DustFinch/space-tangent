// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static async Task ExerciseLegacyDisabledPlugins(SolarSystemScene scene, string directory, string manifest)
    {
        var metadata = ObserverPluginManifest.Load(manifest);
        var original = scene.OpenPlugin(manifest) ?? throw new InvalidOperationException("The original plugin must open.");
        string profile = Path.Combine(directory, "archived-disabled-plugins.json");
        File.WriteAllText(profile, System.Text.Json.JsonSerializer.Serialize(new
        {
            Site = new { },
            DisabledPlugins = $"{metadata.Id}, {metadata.Id}, ../invalid"
        }));
        await scene.ImportStartupSettings(profile);
        var file = new LocalAddonSettingsFile();
        if (original.Enabled || !file.Load().DisabledPlugins.SequenceEqual([metadata.Id]) ||
            scene.OpenPlugin(manifest) is not null || !await scene.ReloadAddOns())
            throw new InvalidOperationException("The imported plugin must stop and remain disabled across add-on reload.");
        if (scene.OpenPlugin(manifest) is not null || scene.OpenPlugin(manifest, reenable: true) is not { Enabled: true })
            throw new InvalidOperationException("Explicitly reopening the manifest must re-enable the original plugin.");
        if (file.Load().DisabledPlugins.Length != 0)
            throw new InvalidOperationException("Explicit re-enable must persist.");
        GD.Print($"Legacy disabled plugins: imported {metadata.Id}, stopped running plugin, retained block on reload and reopened explicitly.");
    }
}
