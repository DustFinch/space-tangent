// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Godot;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseLiveSatelliteUpdate()
    {
        string root = System.Environment.GetEnvironmentVariable("SPACETANGENT_TEST_UPDATE_OUTPUT")
            ?? throw new InvalidOperationException("The opt-in live satellite update needs an explicit output directory.");
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        const string variable = "SPACETANGENT_TLE_UPDATES";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        string cache = Path.Combine(root, "cache");
        try
        {
            System.Environment.SetEnvironmentVariable(variable, cache);
            string[] groups = ["stations", "visual", "science", "gps-ops"];
            bool existingCapture = groups.All(group => File.Exists(Path.Combine(cache, group + ".tle")));
            if (existingCapture) Require(await _scene.ReloadAddOns(), "A retained public GP capture must load before checking its update throttle.");
            var before = Session.CaptureScenario(0);
            var oldWorld = Session.World;
            var button = Find<Button>("UpdateSatelliteGroups");
            var status = Find<Label>("WorkspaceStatus");
            button.EmitSignal(Button.SignalName.Pressed);
            button.EmitSignal(Button.SignalName.Pressed);
            await Until(() => status.Text.StartsWith("Updated satellite groups:", StringComparison.Ordinal) ||
                status.Text is "Satellite groups are current." or "Satellite group update failed." ||
                status.Text.StartsWith("Could not update satellite groups.", StringComparison.Ordinal),
                "Downloading the four supported public GP groups", 150);
            Require((existingCapture ? status.Text == "Satellite groups are current." :
                    status.Text == "Updated satellite groups: 4." && !ReferenceEquals(oldWorld, Session.World)) && Session.World.LocalSatellites.Count > 0,
                "All four live GP groups must load and republish before this probe can pass: " + status.Text + " " + status.TooltipText);
            var hashes = groups.ToDictionary(group => group, group => SHA256.HashData(File.ReadAllBytes(Path.Combine(cache, group + ".tle"))));
            var times = groups.ToDictionary(group => group, group => File.GetLastWriteTimeUtc(Path.Combine(cache, group + ".tle")));
            var actual = Session.CaptureScenario(0);
            Require(actual.Views.Count == before.Views.Count && actual.DeltaT == before.DeltaT && actual.Synchronized == before.Synchronized &&
                actual.Views.Select(view => (view.Epoch, view.Navigation, view.Selection)).SequenceEqual(before.Views.Select(view => (view.Epoch, view.Navigation, view.Selection))),
                "Updating orbital elements must preserve every observer's epoch, navigation, selection and time model.");
            File.WriteAllText(Path.Combine(root, "provenance.json"), JsonSerializer.Serialize(new
            {
                capturedAtUtc = DateTimeOffset.UtcNow,
                kind = "new live UI capture, not a restored historical archive",
                acceptedFromExistingCapture = existingCapture,
                records = Session.World.LocalSatellites.Count,
                groups = groups.Select(group => new { group, url = $"https://celestrak.org/NORAD/elements/gp.php?GROUP={group}&FORMAT=TLE",
                    sha256 = Convert.ToHexString(hashes[group]).ToLowerInvariant(), downloadedFileTimeUtc = times[group] })
            }, new JsonSerializerOptions { WriteIndented = true }));
            button.EmitSignal(Button.SignalName.Pressed);
            await Until(() => status.Text == "Satellite groups are current.", "Respecting the two-hour public update interval", 15);
            Require(groups.All(group => times[group] == File.GetLastWriteTimeUtc(Path.Combine(cache, group + ".tle")) &&
                hashes[group].AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(cache, group + ".tle"))))),
                "The throttled update must retain all downloaded bytes and timestamps without another request.");
            string blocked = Path.Combine(root, "blocked-cache"); File.WriteAllText(blocked, "keep");
            System.Environment.SetEnvironmentVariable(variable, blocked);
            button.EmitSignal(Button.SignalName.Pressed);
            await Until(() => status.Text.StartsWith("Could not update satellite groups.", StringComparison.Ordinal), "Reporting an obstructed satellite cache", 15);
            Require(File.ReadAllText(blocked) == "keep", "A failed update must retain the obstructing file.");
            GD.Print($"Live satellite update GPU: four GP groups, {Session.World.LocalSatellites.Count} local records, camera preservation, two-hour throttle and blocked-cache recovery; output={root}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
            Require(await _scene.ReloadAddOns(), "Restore the original satellite sources after the opt-in live update.");
        }
    }
}
