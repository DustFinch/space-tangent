// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseUiFailures(string directory, string settings, string bookmarks)
    {
        var before = Session.CaptureScenario(0);
        var status = Find<Label>("WorkspaceStatus");
        await Click(Find<Button>("BookmarksTab"));
        await Click(Find<Button>("StoreBookmark"));
        Require(status.Text.StartsWith("Could not save bookmark.", StringComparison.Ordinal), "An empty bookmark name must show its refusal.");
        await FillField("BookmarkName", "Recovery witness"); await Click(Find<Button>("StoreBookmark"));
        Require(new ObserverBookmarkFile(bookmarks).Load().Count == 1, "A valid bookmark must still save after refusal.");
        var delete = Find<Button>("RestoreBookmark").GetParent().GetChildren().OfType<Button>().Single(button => button.Text == "Delete");
        await Click(delete);
        Require(new ObserverBookmarkFile(bookmarks).Load().Count == 0 && delete.Disabled, "Deleting the last bookmark must disable its controls.");
        await Click(Find<Button>("StoreBookmark"));
        File.Delete(bookmarks); Directory.CreateDirectory(bookmarks);
        await Click(Find<Button>("StoreBookmark"));
        Require(status.Text.StartsWith("Could not save bookmark.", StringComparison.Ordinal), "An unwritable bookmark destination must report failure.");
        await Click(delete);
        Require(status.Text.StartsWith("Could not delete bookmark.", StringComparison.Ordinal), "A failed delete must keep the in-memory bookmark.");
        Directory.Delete(bookmarks); await Click(delete);
        Require(delete.Disabled && status.Text == "Bookmark deleted.", "Delete must recover when the destination becomes writable.");

        string invalid = Path.Combine(directory, "invalid.stscene"); File.WriteAllText(invalid, "{broken");
        Require(!_scene.LoadScenario(invalid) && status.Text.StartsWith("Could not open scenario.", StringComparison.Ordinal),
            "A malformed scenario must show its error.");
        Require(!_scene.SaveScenario(directory) && status.Text.StartsWith("Could not save scenario.", StringComparison.Ordinal),
            "A directory cannot be overwritten by a scenario.");
        Require(_scene.RunScript(invalid) is null && status.Text.StartsWith("Could not open script.", StringComparison.Ordinal),
            "Invalid Lua must not leave a running foreground script.");
        Require(!_scene.StartReplay(invalid) && !_scene.Replaying, "Invalid replay must close its partially opened file.");
        Require(await _scene.StopRecording() is null, "Stopping an inactive recording is a no-op.");
        Require(!_scene.QueueScreenshot("bad\0capture.png"), "An invalid screenshot path must be refused before capture.");
        RequireScenario(before, Session.CaptureScenario(0), "Failed input and filesystem operations");

        if (File.Exists(settings)) File.Delete(settings);
        Directory.CreateDirectory(settings);
        _scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
        Require(Session.DeltaT == DeltaTModel.MorrisonStephenson2004 && status.Text.StartsWith("Could not save ΔT.", StringComparison.Ordinal),
            "An unwritable preference file must report failure while retaining the selected in-memory Delta T.");
        Directory.Delete(settings);
        _scene.SetDeltaT(DeltaTModel.EspenakMeeus2006);
        Require(File.Exists(settings), "Saving Delta T must recover after removing the blocked destination.");

        string? startup = System.Environment.GetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE");
        try
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE", directory);
            Require(!_scene.SaveStartupWorkspace(), "Saving the startup scene to a directory must fail.");
            Find<MenuButton>("ScenarioMenu").GetPopup().EmitSignal(PopupMenu.SignalName.IdPressed, 4L);
            Require(status.Text.StartsWith("Could not clear startup views.", StringComparison.Ordinal), "A blocked startup path must report a clear failure.");
        }
        finally { System.Environment.SetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE", startup); }
        GD.Print("UI failure recovery: bookmarks, malformed scenes/scripts/replay, capture paths, Delta T and startup persistence.");
    }
}
