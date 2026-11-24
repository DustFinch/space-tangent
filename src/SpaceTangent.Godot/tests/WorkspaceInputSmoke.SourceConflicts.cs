// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

using SpaceTangent.Application.Addons;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseDuplicateSources(string directory, string science)
    {
        string copy = Path.Combine(directory, "duplicate-science");
        CopyDirectory(science, copy);
        var file = new LocalAddonSettingsFile();
        var settings = LocalAddonSettings.Empty.SetDefault(AddonSourceKind.Science, false);
        settings = settings.SetSource(file.FindOrCreate(settings, AddonSourceKind.Science, science), true);
        settings = settings.SetSource(file.FindOrCreate(settings, AddonSourceKind.Science, copy), true);
        var decoded = await AddonLoader.Load(Session.World.Ephemeris, null, settings, file, null, CancellationToken.None);
        try
        {
            Require(decoded.World.Satellites.Count == 2 && decoded.World.SourceWarnings.Count == 2,
                "Two original science folders must retain Jason/SORCE once and report both duplicate definitions.");
            Require(decoded.World.Satellites.All(track => !track.MeshPath.StartsWith(copy, StringComparison.OrdinalIgnoreCase)),
                "The first science source must own both the trajectory and model.");
            Require(Session.World.Satellites.Count == 2, "Testing duplicate sources must leave the live world untouched.");
            GD.Print("UI: duplicate original science folders retained consistent first definitions with source diagnostics.");
        }
        finally { decoded.World.DistantStars?.Dispose(); }
    }
}
