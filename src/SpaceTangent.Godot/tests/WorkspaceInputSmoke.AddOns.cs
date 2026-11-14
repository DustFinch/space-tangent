// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseLocalAddOns(string directory, string settings, string originalScience, string originalMission)
    {
        string science = Path.Combine(directory, "данные спутников", "same-name");
        string mission = Path.Combine(directory, "данные миссий", "same-name");
        CopyDirectory(originalScience, science);
        CopyDirectory(Path.GetDirectoryName(originalMission)!, mission);
        string missionFilename = Path.GetFileName(originalMission);
        Require(Session.World.MissionId is not null, "The original mission must load before testing local sources.");

        await Click(Find<Button>("AddOnsTab"));
        var scienceDialog = _scene.GetNode<FileDialog>("OpenAddonFolder");
        scienceDialog.CurrentDir = science;
        await Click(Find<Button>("BrowseLocalAddon"));
        await Until(() => scienceDialog.Visible, "Opening the science folder chooser");
        await Click(scienceDialog.GetOkButton());
        await WaitForAddOns("Installing the original science source");
        Require(Find<CheckButton>("LocalAddon0").ButtonPressed && Session.World.Satellites.Count == 2,
            "The added science folder must provide the two original spacecraft.");

        var kind = Find<OptionButton>("LocalAddonKind");
        await Click(kind);
        await ChooseMenuItem(kind.GetPopup(), (int)LocalAddonKind.Mission);
        var missionDialog = _scene.GetNode<FileDialog>("OpenAddonMission");
        missionDialog.CurrentDir = mission;
        await Click(Find<Button>("BrowseLocalAddon"));
        await Until(() => missionDialog.Visible, "Opening the mission file chooser");
        await Click(missionDialog.GetLineEdit());
        await KeyPress(Key.A, ctrl: true);
        await TypeText(missionFilename);
        await KeyPress(Key.Enter);
        await WaitForAddOns("Installing the original mission source");

        var scienceSwitch = Find<CheckButton>("LocalAddon0");
        var missionSwitch = Find<CheckButton>("LocalAddon1");
        Require(scienceSwitch.Text == missionSwitch.Text && scienceSwitch.TooltipText != missionSwitch.TooltipText &&
                missionSwitch.ButtonPressed && Session.World.MissionId is not null,
            "Same-named sources must be represented by independent rows and retain their distinct paths.");
        Require(Session[0].Selection == Jason, "The Jason-1 selection must still be active before disabling its source.");
        await Click(scienceSwitch);
        await WaitForAddOns("Disabling the science source");
        Require(Session.World.Satellites.Count == 0 && Session[0].Selection is null && Session.World.MissionId is not null &&
                Find<CheckButton>("LocalAddon1").ButtonPressed,
            "Disabling science must clear its selection while the same-named Cassini source remains enabled.");

        await Click(Find<CheckButton>("LocalAddon0"));
        await WaitForAddOns("Re-enabling the science source");
        Require(Session.World.Satellites.Any(satellite => satellite.Id == Jason) && Session.World.MissionId is not null,
            "Re-enabling science must restore Jason-1 beside the mission.");
        await Click(Find<CheckButton>("LocalAddon1"));
        await WaitForAddOns("Disabling the mission source");
        Require(Session.World.Satellites.Count == 2 && Session.World.MissionId is null &&
                Find<CheckButton>("LocalAddon0").ButtonPressed,
            "Disabling Cassini must leave the same-named science source enabled.");
        await Click(Find<CheckButton>("LocalAddon1"));
        await WaitForAddOns("Re-enabling the mission source");
        Require(Session.World.MissionId is not null, "Re-enabling the source must restore Cassini.");

        var world = Session.World;
        string state = File.ReadAllText(settings);
        string texture = Path.Combine(science, "data", "models", "jas_brsh.png");
        File.Move(texture, texture + ".bak");
        await Click(Find<Button>("ReloadAddOns"));
        await Until(() => _scene.LastAddonDiagnostic is not null, "Reporting the missing real texture", 20);
        Require(ReferenceEquals(world, Session.World) && File.ReadAllText(settings) == state &&
                _scene.LastAddonDiagnostic?.File == texture,
            "A failed update must retain the last good world and point to the missing texture.");
        File.Move(texture + ".bak", texture);
        await Click(Find<Button>("ReloadAddOns"));
        await WaitForAddOns("Recovering the real source");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PACKED_SCIENCE") is { Length: > 0 } packed)
        {
            var observer = Session.CaptureScenario(0);
            CopyDirectory(packed, science);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Installing the archived packed science models");
            int count = System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_DEEP_PROBE") is { Length: > 0 } ? 3 : 2;
            Require(Session.World.Satellites.Count == count && Session.World.Satellites.All(track =>
                Path.GetExtension(track.MeshPath).Equals(".glb", StringComparison.OrdinalIgnoreCase)),
                "The archived pack must replace both source meshes while keeping their catalogue IDs.");
            RequireScenario(observer, Session.CaptureScenario(0), "Switching from source OBJ to packed GLB");
            File.Move(Path.Combine(science, "pack.json"), Path.Combine(science, "pack.json.disabled"));
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Returning to the original source models");
            Require(Session.World.Satellites.All(track => Path.GetExtension(track.MeshPath) == ".obj"),
                "Removing the packed manifest must restore both source OBJ models.");
            RequireScenario(observer, Session.CaptureScenario(0), "Switching from packed GLB back to source OBJ");
            GD.Print("UI: installed the original Jason-1 and SORCE packed GLBs and returned to the source without losing either view");
        }
        RemoveChild(_scene);
        _scene.QueueFree();
        _scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
        AddChild(_scene);
        await Until(() => _scene.InitializationFinished, "Reopening with the saved source settings", 25);
        Require(Session.World.Satellites.Any(satellite => satellite.Id == Jason) && Session.World.MissionId is not null &&
                Find<CheckButton>("LocalAddon0").ButtonPressed && Find<CheckButton>("LocalAddon1").ButtonPressed &&
                !Find<CheckButton>("DefaultAddonScience").ButtonPressed && !Find<CheckButton>("DefaultAddonMission").ButtonPressed,
            "Restarting must open both local sources from their persisted settings.");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PORTABLE_ADDON_PROFILE") is { Length: > 0 } capture)
        {
            var file = new LocalAddonSettingsFile();
            var portable = LocalAddonSettings.Empty;
            portable = portable.SetSource(file.FindOrCreate(portable, LocalAddonKind.Science, originalScience), true);
            portable = portable.SetSource(file.FindOrCreate(portable, LocalAddonKind.Mission, originalMission), true);
            Require(portable.Sources.All(source => source.RelativeToData),
                "Sources inside the portable data directory must be saved relative to it.");
            file.Save(portable);
            File.Copy(settings, capture, overwrite: true);
        }
        GD.Print("UI: installed original Jason-1, SORCE and Cassini, checked independent same-name switches, rollback and restart");
    }

    private Task WaitForAddOns(string action) =>
        Until(() => Find<Label>("WorkspaceStatus").Text == "Add-ons reloaded.", action, 20);

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
