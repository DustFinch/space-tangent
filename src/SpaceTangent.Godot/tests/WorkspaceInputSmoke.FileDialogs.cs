// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseFileDialogActions(string directory)
    {
        var status = Find<Label>("WorkspaceStatus");
        var scenarioMenu = Find<MenuButton>("ScenarioMenu").GetPopup();
        var importer = Find<FileDialog>("ImportObserverSettings");
        scenarioMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        Require(importer.Visible, "The scenario menu must open its observer settings dialog.");
        importer.Hide();
        string invalid = Path.Combine(directory, "invalid-observer.json"); File.WriteAllText(invalid, "{broken");
        importer.EmitSignal(FileDialog.SignalName.FileSelected, invalid);
        Require(status.Text.StartsWith("Could not import observer settings.", StringComparison.Ordinal),
            "A damaged observer document must report its import failure through the normal dialog.");
        string valid = Path.Combine(directory, "dialog-observer.json");
        File.WriteAllText(valid, """{"DeltaT":"MorrisonStephenson2004"}""");
        importer.EmitSignal(FileDialog.SignalName.FileSelected, valid);
        Require(Session.DeltaT == DeltaTModel.MorrisonStephenson2004, "The observer dialog must apply a valid model.");

        var scripts = Find<MenuButton>("ScriptMenu").GetPopup();
        var open = Find<FileDialog>("OpenScript");
        scripts.EmitSignal(PopupMenu.SignalName.IdPressed, 0L);
        Require(open.Visible, "The script menu must open its local file dialog."); open.Hide();
        string lua = Path.Combine(directory, "dialog.lua"); File.WriteAllText(lua, "st.wait(5)");
        open.EmitSignal(FileDialog.SignalName.FileSelected, lua);
        Require(status.Text == "Lua script running.", "Selecting a Lua file must start a foreground script.");
        scripts.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        Require(status.Text == "Lua script stopped.", "The script menu must stop its foreground script.");
        scripts.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        var conversion = Find<FileDialog>("ConvertTourSource");
        Require(conversion.Visible, "The script menu must open the tour conversion source."); conversion.Hide();
        scripts.EmitSignal(PopupMenu.SignalName.IdPressed, 999999L);
        string root = Path.Combine(directory, "dialog-plugin"); Directory.CreateDirectory(root);
        string manifest = Path.Combine(root, "plugin.json");
        File.WriteAllText(manifest, """{"format":1,"api":1,"id":"dialog-plugin","name":"Dialog plugin","version":"1","main":"main.lua"}""");
        File.WriteAllText(Path.Combine(root, "main.lua"), "return 1");
        open.EmitSignal(FileDialog.SignalName.FileSelected, manifest);
        Require(status.Text == "Plugin enabled · Dialog plugin", "The same file dialog must route a manifest to the plugin host.");
        int item = Enumerable.Range(0, scripts.ItemCount).Single(index => scripts.GetItemText(index) == "Disable Dialog plugin");
        scripts.EmitSignal(PopupMenu.SignalName.IdPressed, (long)scripts.GetItemId(item));
        Require(new LocalAddonSettingsFile().Load().DisabledPlugins.Contains("dialog-plugin"), "A dialog-opened plugin must be disabled through its normal menu.");
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        GD.Print("UI: observer and script file dialogs, malformed import, Lua start/stop, conversion menu and plugin manifest routing passed.");
    }
}
