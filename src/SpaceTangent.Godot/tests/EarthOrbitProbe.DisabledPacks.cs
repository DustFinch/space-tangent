// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseLegacyDisabledPacks(SolarSystemScene scene, string directory, string packRoot)
    {
        var session = scene.Session!;
        var file = new LocalAddonSettingsFile();
        var original = file.Load();
        var source = file.FindOrCreate(original, LocalAddonKind.BodyCatalog, packRoot);
        var installed = original.SetSource(source, true);
        if (!await scene.ReloadAddOns(installed))
            throw new InvalidOperationException("The original comet pack could not be installed.");
        var comet = session.World.Catalogue.Objects.SingleOrDefault(body =>
            body.Kind == CatalogueKind.Comet && body.Name.Contains("Halley", StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new InvalidOperationException("Halley is missing from the installed original comet catalogue.");
        var scenario = session.CaptureScenario(0);
        string profile = Path.Combine(directory, "archived-disabled-packs.json");
        File.WriteAllText(profile, """{"Site":{},"DisabledPacks":"p4-original-comets, p4-original-comets, ../invalid"}""");
        await scene.ImportStartupSettings(profile);
        if (session.World.Catalogue.Find(comet) is not null ||
            !file.Load().DisabledPacks.SequenceEqual(["p4-original-comets"]) ||
            session.World.Satellites.Count != 2 || session.DeltaT != scenario.DeltaT)
            throw new InvalidOperationException("Disabling the archived pack must preserve other sources and ΔT.");
        if (!await scene.ReloadAddOns() || session.World.Catalogue.Find(comet) is not null)
            throw new InvalidOperationException("The archived disable flag must survive add-on reload.");
        var rows = scene.FindChild("LocalAddonSources", recursive: true, owned: false);
        var toggle = rows?.FindChildren("LocalAddon*", "CheckButton", recursive: true, owned: false)
            .OfType<CheckButton>().SingleOrDefault(button =>
                string.Equals(button.TooltipText, file.Resolve(source), StringComparison.OrdinalIgnoreCase));
        if (toggle is null || toggle.ButtonPressed || toggle.Disabled)
            throw new InvalidOperationException("The panel must offer an unchecked switch for the filtered comet pack.");
        toggle.ButtonPressed = true;
        ulong deadline = Time.GetTicksMsec() + 60_000;
        while (GodotObject.IsInstanceValid(toggle) && toggle.IsInsideTree())
        {
            if (Time.GetTicksMsec() >= deadline)
                throw new InvalidOperationException("Reload after enabling the original comet pack timed out.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        if (session.World.Catalogue.Find(comet) is null || file.Load().DisabledPacks.Length != 0)
            throw new InvalidOperationException("The explicit switch must clear the archived override and restore the pack. " +
                (scene.LastAddonDiagnostic?.Detail ?? "No add-on diagnostic was recorded."));
        await scene.ImportStartupSettings(profile);
        File.WriteAllText(profile, """{"Site":{},"DisabledPacks":""}""");
        await scene.ImportStartupSettings(profile);
        if (session.World.Catalogue.Find(comet) is null || file.Load().DisabledPacks.Length != 0)
            throw new InvalidOperationException("Clearing the archived list must restore the comet pack.");
        GD.Print("Legacy disabled packs: original comet manifest ID, Halley removed and restored, panel and profile re-enable, other sources and ΔT preserved.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SECOND_PACK_PROBE") is { Length: > 0 } asteroidRoot)
            await ExerciseSameNamedPacks(scene, directory, file, packRoot, asteroidRoot, scenario.DeltaT);
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_TERRAIN_TIMELINE_PROBE") == "1")
            await ExerciseTerrainAndTimelineSwitches(scene, directory, file, scenario.DeltaT);
    }

    private static async Task ExerciseSameNamedPacks(SolarSystemScene scene, string directory, LocalAddonSettingsFile file,
        string cometRoot, string asteroidRoot, SpaceTangent.Core.Time.DeltaTModel deltaT)
    {
        string first = CopyPack(cometRoot, "comets");
        string second = CopyPack(asteroidRoot, "asteroids");
        var session = scene.Session!;
        var original = file.Load();
        var settings = original with { Sources = [] };
        foreach (string root in new[] { first, second })
            settings = settings.SetSource(file.FindOrCreate(settings, LocalAddonKind.BodyCatalog, root), true);
        if (!await scene.ReloadAddOns(settings))
            throw new InvalidOperationException("The original comet and asteroid packs could not be loaded together.");
        var comet = session.World.Catalogue.Objects.Single(body => body.Kind == CatalogueKind.Comet &&
            body.Name.Contains("Halley", StringComparison.OrdinalIgnoreCase)).Id;
        var asteroid = session.World.Catalogue.Objects.Single(body => body.Kind == CatalogueKind.Asteroid &&
            body.Name.Contains("Pallas", StringComparison.OrdinalIgnoreCase)).Id;
        string key = ArchivedPackPathKey(first);
        string profile = Path.Combine(directory, "archived-same-named-packs.json");
        File.WriteAllText(profile, System.Text.Json.JsonSerializer.Serialize(new { Site = new { }, DisabledPacks = key }));
        await scene.ImportStartupSettings(profile);
        if (session.World.Catalogue.Find(comet) is not null || session.World.Catalogue.Find(asteroid) is null ||
            !file.Load().DisabledPacks.SequenceEqual([key]) || session.DeltaT != deltaT)
            throw new InvalidOperationException("The legacy path switch must affect only the matching source.");

        string manifest = Path.Combine(first, "pack.json");
        string backup = manifest + ".bak";
        File.Move(manifest, backup);
        try
        {
            File.WriteAllText(manifest, "{ invalid pack");
            if (!await scene.ReloadAddOns() || session.World.Catalogue.Find(comet) is not null ||
                session.World.Catalogue.Find(asteroid) is null)
                throw new InvalidOperationException("Reload must skip the corrupt disabled pack.");
            var published = session.World;
            var enabled = file.Load() with { DisabledPacks = [] };
            if (await scene.ReloadAddOns(enabled) || !ReferenceEquals(session.World, published) ||
                !file.Load().DisabledPacks.SequenceEqual([key]) || session.World.Catalogue.Find(asteroid) is null)
                throw new InvalidOperationException("A failed re-enable must preserve the published world and saved switches.");
        }
        finally
        {
            File.Move(backup, manifest, overwrite: true);
        }
        if (!await scene.ReloadAddOns(file.Load() with { DisabledPacks = [] }) ||
            session.World.Catalogue.Find(comet) is null || session.World.Catalogue.Find(asteroid) is null)
            throw new InvalidOperationException("Restoring the source must make both real packs available again.");
        GD.Print("Same-named packs: legacy path key isolated Halley from Pallas, malformed re-enable rolled back, both restored.");

        string CopyPack(string root, string owner)
        {
            string destination = Path.Combine(directory, owner, "same-name");
            Directory.CreateDirectory(destination);
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(destination, Path.GetRelativePath(root, path));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
            }
            return destination;
        }
    }

    private static async Task ExerciseTerrainAndTimelineSwitches(SolarSystemScene scene, string directory,
        LocalAddonSettingsFile file, SpaceTangent.Core.Time.DeltaTModel deltaT)
    {
        var session = scene.Session!;
        var timeline = session.World.TimelineId ?? throw new InvalidOperationException("The original Cassini timeline must load.");
        if (!session.World.HasLunarTerrain)
            throw new InvalidOperationException("The original lunar relief must load.");

        string profile = Path.Combine(directory, "archived-terrain-timeline.json");
        foreach (LocalAddonKind kind in new[] { LocalAddonKind.Timeline, LocalAddonKind.Terrain })
        {
            string key = ArchivedPackPathKey(AddonLoader.DefaultPath(kind).Resolve());
            File.WriteAllText(profile, System.Text.Json.JsonSerializer.Serialize(new { Site = new { }, DisabledPacks = key }));
            await scene.ImportStartupSettings(profile);
            if (!await scene.ReloadAddOns() || session.World.HasLunarTerrain != (kind != LocalAddonKind.Terrain) ||
                session.World.TimelineId != (kind == LocalAddonKind.Timeline ? null : timeline) ||
                !file.Load().DisabledPacks.SequenceEqual([key]) || session.World.Satellites.Count != 2 || session.DeltaT != deltaT)
                throw new InvalidOperationException($"Disabling {kind} must preserve the other source, spacecraft and delta-T.");
        }

        File.WriteAllText(profile, """{"Site":{},"DisabledPacks":""}""");
        await scene.ImportStartupSettings(profile);
        if (!session.World.HasLunarTerrain || session.World.TimelineId != timeline || file.Load().DisabledPacks.Length != 0)
            throw new InvalidOperationException("Clearing the archive profile must restore both timeline and terrain.");
        GD.Print("Terrain and timeline pack switches: original lunar relief and Cassini disabled independently after reload and restored.");
    }

    private static string ArchivedPackPathKey(string root)
    {
        string legacyPath = Path.GetFullPath(root).Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            legacyPath = legacyPath.ToUpperInvariant();
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyPath + "\0"))).ToLowerInvariant();
        return "addon-root:" + fingerprint;
    }
}
