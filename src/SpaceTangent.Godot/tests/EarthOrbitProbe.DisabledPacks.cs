// SPDX-License-Identifier: GPL-2.0-or-later
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
        var toggle = (CheckButton)scene.FindChild("LocalAddon0", recursive: true, owned: false);
        if (toggle.ButtonPressed)
            throw new InvalidOperationException("The panel must show the filtered source as disabled.");
        toggle.ButtonPressed = true;
        for (int frame = 0; frame < 500 && session.World.Catalogue.Find(comet) is null; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (session.World.Catalogue.Find(comet) is null || file.Load().DisabledPacks.Length != 0)
            throw new InvalidOperationException("The explicit switch must clear the archived override and restore the pack.");
        await scene.ImportStartupSettings(profile);
        File.WriteAllText(profile, """{"Site":{},"DisabledPacks":""}""");
        await scene.ImportStartupSettings(profile);
        if (session.World.Catalogue.Find(comet) is null || file.Load().DisabledPacks.Length != 0)
            throw new InvalidOperationException("Clearing the archived list must restore the comet pack.");
        GD.Print("Legacy disabled packs: original comet manifest ID, Halley removed and restored, panel and profile re-enable, other sources and ΔT preserved.");
    }
}
