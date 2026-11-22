// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.Observing;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

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
        await ExerciseTourAndPluginRecovery(directory);
        await ExerciseTimeAndOfflineControls(directory);

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

    private async Task ExerciseTourAndPluginRecovery(string directory)
    {
        var status=Find<Label>("WorkspaceStatus");
        var source=Find<FileDialog>("ConvertTourSource"); var target=Find<FileDialog>("ConvertedTourTarget");
        string input=Path.Combine(directory,"tour.cel"); string output=Path.Combine(directory,"tour.lua");
        File.WriteAllText(input,"{ print { text \"Local tour\" } }");
        source.EmitSignal(FileDialog.SignalName.FileSelected,input); Require(target.Visible,"Conversion must open its destination dialog."); target.Hide();
        target.EmitSignal(FileDialog.SignalName.FileSelected,output);
        Require(File.Exists(output)&&status.Text=="Tour saved as Lua.","The converted Lua file must be written.");
        source.EmitSignal(FileDialog.SignalName.FileSelected,input); target.Hide();
        target.EmitSignal(FileDialog.SignalName.FileSelected,directory);
        Require(status.Text.StartsWith("Could not save tour.",StringComparison.Ordinal),"A failed conversion write must show its diagnostic.");
        File.WriteAllText(input,"{ preloadtex {} }");
        source.EmitSignal(FileDialog.SignalName.FileSelected,input); target.Hide(); target.EmitSignal(FileDialog.SignalName.FileSelected,output);
        Require(status.Text.Contains("conversion notes",StringComparison.Ordinal),"Unapplied options must be reported beside the saved tour.");
        source.EmitSignal(FileDialog.SignalName.FileSelected,input); target.Hide(); target.EmitSignal(FileDialog.SignalName.Canceled);
        byte[] saved=File.ReadAllBytes(output); target.EmitSignal(FileDialog.SignalName.FileSelected,output);
        Require(saved.SequenceEqual(File.ReadAllBytes(output)),"Cancelled conversion must discard the pending writer.");
        File.WriteAllText(input,"{broken"); source.EmitSignal(FileDialog.SignalName.FileSelected,input);
        Require(status.Text.StartsWith("Could not convert tour.",StringComparison.Ordinal),"Malformed tours must not open a destination.");

        var paths=new List<string>();
        for(int index=0;index<9;index++)
        {
            string root=Path.Combine(directory,"plugin-"+index); Directory.CreateDirectory(root);
            string manifest=Path.Combine(root,"plugin.json"); paths.Add(manifest);
            File.WriteAllText(manifest,$$"""{"format":1,"api":1,"id":"witness-{{index}}","name":"Witness {{index}}","version":"1","main":"main.lua"}""");
            File.WriteAllText(Path.Combine(root,"main.lua"),"return 1");
            if(index<8) Require(_scene.OpenPlugin(manifest)is not null,"A valid plugin below the eight-plugin bound must load.");
        }
        Require(_scene.OpenPlugin(paths[8])is null&&status.Text.Contains("eight plugins",StringComparison.Ordinal),"The ninth concurrent plugin must be refused visibly.");
        var popup=Find<MenuButton>("ScriptMenu").GetPopup();
        int first=Enumerable.Range(0,popup.ItemCount).Single(index=>popup.GetItemText(index)=="Disable Witness 0");
        popup.EmitSignal(PopupMenu.SignalName.IdPressed,(long)popup.GetItemId(first));
        Require(_scene.OpenPlugin(paths[0])is null&&status.Text.StartsWith("Plugin disabled",StringComparison.Ordinal),"A disabled plugin must remain disabled on an ordinary reopen.");
        Require(_scene.OpenPlugin(paths[0],reenable:true)is not null,"Explicit reenabling must persist and restart the plugin.");
        await ToSignal(Tree,SceneTree.SignalName.ProcessFrame);
        for(int index=0;index<8;index++)
        {
            int item=Enumerable.Range(0,popup.ItemCount).Single(row=>popup.GetItemText(row)=="Disable Witness "+index);
            popup.EmitSignal(PopupMenu.SignalName.IdPressed,(long)popup.GetItemId(item));
        }
        GD.Print("UI: tour conversion save/failure/cancel and eight-plugin capacity with persistent disable and reenable.");
    }

    private async Task ExerciseTimeAndOfflineControls(string directory)
    {
        string saved=Path.Combine(directory,"before-time-errors.stscene"); ScenarioFile.Save(saved,Session.CaptureScenario(0));
        var status=Find<Label>("WorkspaceStatus");
        try
        {
            await Click(Find<Button>("TimeTab"));
            var date=Find<LineEdit>("UtcDate"); var clock=Find<LineEdit>("UtcTime");
            var before=Session[0];
            foreach(var pair in new[]{("bad","12:00:00"),("2024-01-01","25:00:00")})
            {
                await FillField("UtcDate",pair.Item1); await FillField("UtcTime",pair.Item2); await KeyPress(Key.Enter);
                Require(Session[0]==before&&status.Text.StartsWith("Enter a date",StringComparison.Ordinal),"Malformed calendar input changed the clock.");
            }
            await FillField("UtcDate","1950-01-01"); await FillField("UtcTime","12:00:00"); await KeyPress(Key.Enter);
            Require(HistoricalDate.TryParse("1950-01-01",out var historical,out _),"The historical date fixture is invalid.");
            var expected=EarthCivilTime.FromModeledUt1(historical,12*3600*JulianInstant.NanosecondsPerSecond,Session.DeltaT);
            Require(Session[0].Epoch==expected,"Pre-UTC input must use the selected modeled UT1 conversion.");
            before=Session[0];
            await FillField("UtcTime","12:00:60"); await KeyPress(Key.Enter);
            Require(Session[0]==before&&status.Text.Contains("outside",StringComparison.Ordinal),"A leap second cannot be interpreted in modeled UT1.");
            var epoch=Find<LineEdit>("TimePanelEpoch"); epoch.Text="not-a-date"; epoch.EmitSignal(LineEdit.SignalName.TextSubmitted,epoch.Text);
            Require(Session[0]==before&&status.Text.StartsWith("Could not set the epoch.",StringComparison.Ordinal),"Invalid TDB input must preserve the existing epoch.");
            var now=date.GetParent().GetParent().FindChildren("*","Button",recursive:true,owned:false).OfType<Button>().Single(button=>button.Text=="Now");
            var low=DateTime.UtcNow.AddSeconds(-3); await Click(now); var high=DateTime.UtcNow.AddSeconds(3);
            var utc=TimeConversion.ToUtc(Session[0].Epoch);
            Require(utc.Date.ToDateTime(new TimeOnly(utc.Hour,utc.Minute,(int)utc.Second))>=low&&
                utc.Date.ToDateTime(new TimeOnly(utc.Hour,utc.Minute,(int)utc.Second))<=high,"Now must set the active observer to the current UTC.");
            await Click(Find<SubViewportContainer>("ViewCanvas0"));
            bool places=Session[0].Sky.SurfacePlaces;
            await KeyPress(Key.L,ctrl:true); Require(Session[0].Sky.SurfacePlaces!=places,"Ctrl+L must toggle surface labels.");
            await KeyPress(Key.L,ctrl:true); Require(Session[0].Sky.SurfacePlaces==places,"A second Ctrl+L must restore the layer.");

            var offline=Find<CheckButton>("HipsOffline");
            // The automation forces network access off. Verify the preference handler cannot defeat that process policy.
            offline.EmitSignal(BaseButton.SignalName.Toggled,false);
            Require(_scene.SurveysOffline&&!new LocalAddonSettingsFile().Load().HipsOffline,"The offline preference and forced process policy must remain distinct.");
            string settings=System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH")!;
            File.Delete(settings); Directory.CreateDirectory(settings);
            offline.EmitSignal(BaseButton.SignalName.Toggled,true);
            Require(_scene.SurveysOffline&&Directory.Exists(settings),"A failed preference write must preserve forced offline mode.");
            Directory.Delete(settings); offline.EmitSignal(BaseButton.SignalName.Toggled,true);
            Require(new LocalAddonSettingsFile().Load().HipsOffline,"Offline persistence must recover after the destination is unblocked.");
        }
        finally {Require(_scene.LoadScenario(saved),"Could not restore the observers after clock controls.");}
        GD.Print("UI: rejected malformed dates, modeled 1950 UT1, Now, TDB refusal, surface-label shortcut and forced-offline preference recovery.");
    }
}
