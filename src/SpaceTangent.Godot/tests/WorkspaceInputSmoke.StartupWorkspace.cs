// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseStartupWorkspace(string directory, string profile)
    {
        const string workspaceVariable = "SPACETANGENT_STARTUP_WORKSPACE";
        const string sitesVariable = "SPACETANGENT_SAVED_EARTH_PLACES";
        string? oldWorkspace = System.Environment.GetEnvironmentVariable(workspaceVariable);
        string? oldSites = System.Environment.GetEnvironmentVariable(sitesVariable);
        string workspace = Path.Combine(directory, "startup.stscene");
        System.Environment.SetEnvironmentVariable(workspaceVariable, workspace);
        System.Environment.SetEnvironmentVariable(sitesVariable, Path.Combine(directory, "sites.tsv"));
        try
        {
            var left = Session[0] with { Selection = Jason, Playing = false };
            var right = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
                { EarthObserver = EarthObserverCamera.Moscow };
            string fixture = Path.Combine(directory, "jason-and-moon.stscene");
            ScenarioFile.Save(fixture, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(fixture), "The workspace test must open both original views.");
            var imported = LegacyObserverProfiles.Read(profile, left, Session.DeltaT);
            var menu = Find<MenuButton>("ScenarioMenu").GetPopup();
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
            var dialog = _scene.GetNode<FileDialog>("ImportObserverSettings");
            Require(dialog.Visible, "The Scenario menu must open the settings import dialog.");
            dialog.Hide();
            dialog.EmitSignal(FileDialog.SignalName.FileSelected, profile);
            await Until(() => Session[0].Optics == imported.Observer.Optics && Session[0].EarthObserver == imported.Observer.EarthObserver,
                "Importing the original observer settings through the menu");
            Require(Session[0].Selection == Jason && Session[1] == right && Session.DeltaT == imported.DeltaT,
                "Profile import must preserve Jason-1 and the full-Moon view.");
            var expected = Session.CaptureScenario(0);
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
            RequireScenario(expected, ScenarioFile.Load(workspace), "Saving startup views through the menu");
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, 4L);
            Require(!File.Exists(workspace), "Clearing the startup workspace must remove the saved scene.");
            RequireScenario(expected, Session.CaptureScenario(0), "Clearing the saved startup views");
            GD.Print("UI: imported the original observer profile, saved both startup views and cleared the saved workspace.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(workspaceVariable, oldWorkspace);
            System.Environment.SetEnvironmentVariable(sitesVariable, oldSites);
        }
    }
}
