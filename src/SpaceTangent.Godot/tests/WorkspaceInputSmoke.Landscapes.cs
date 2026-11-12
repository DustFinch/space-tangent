// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExercisePanoramas(string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_LANDSCAPES")))
            return;
        string restore = Path.Combine(directory, "before-landscapes.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            _scene.SetMode(0, CameraMode.Surface);
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            var other = Session[1];
            await KeyPress(Key.P, ctrl: true);
            await Until(() => _scene.Renderer(0).VisibleLandscape is not null, "Opening the first local panorama", 15);
            string? first = Session[0].EarthObserver?.LandscapeId;
            await KeyPress(Key.P, ctrl: true);
            await Until(() => _scene.Renderer(0).VisibleLandscape is not null, "Opening the next local panorama", 15);
            Require(first is not null && Session[0].EarthObserver?.LandscapeId != first && Session[1] == other,
                "Cycling panoramas must affect only the active ground view.");
            await KeyPress(Key.P, ctrl: true, alt: true);
            Require(Session[0].EarthObserver?.LandscapeId is null && _scene.Renderer(0).VisibleLandscape is null,
                "The clear shortcut must restore the unobstructed observer.");
            GD.Print("UI: cycled original local panoramas and cleared the active ground view without touching the other observer");
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after the landscape test.");
        }
    }
}
