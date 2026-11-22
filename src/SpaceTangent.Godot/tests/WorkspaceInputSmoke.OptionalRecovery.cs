// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseOptionalResourceRecovery(string directory)
    {
        string saved = Path.Combine(directory, "before-optional-recovery.stscene");
        ScenarioFile.Save(saved, Session.CaptureScenario(0));
        string? oldLandscapes = System.Environment.GetEnvironmentVariable("SPACETANGENT_LANDSCAPES");
        string? oldOptics = System.Environment.GetEnvironmentVariable("SPACETANGENT_OPTICS_FILE");
        string root = Path.Combine(directory, "isolated-landscapes"); Directory.CreateDirectory(root);
        string optics = Path.Combine(directory, "damaged-optics.json"); File.WriteAllText(optics, "{broken");
        var status = Find<Label>("WorkspaceStatus");
        try
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_LANDSCAPES", root);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_OPTICS_FILE", optics);
            var diagram = new ObserverView(Session[0].Epoch, CameraMode.Solar, null, false);
            string path = Path.Combine(directory, "optional-recovery.stscene");
            ScenarioFile.Save(path, new([diagram, diagram], false, 0, 0, Session.DeltaT));
            Require(_scene.LoadScenario(path), "The optional-resource witness must open a plain diagram.");
            Tree.Root.GuiGetFocusOwner()?.ReleaseFocus();
            await KeyPress(Key.P, ctrl: true);
            Require(status.Text.StartsWith("Panoramas apply", StringComparison.Ordinal), "A diagram cannot accept a ground panorama.");
            await KeyPress(Key.Key0, ctrl: true);
            Require(status.Text.StartsWith("A virtual eyepiece needs", StringComparison.Ordinal), "A diagram cannot accept a perspective eyepiece.");
            _scene.SetMode(0, CameraMode.Surface);
            var other = Session[1];
            await KeyPress(Key.Key0, ctrl: true, shift: true);
            Require(status.Text.StartsWith("Could not read local optics profiles.", StringComparison.Ordinal) && Session[1] == other,
                "A malformed optical library must report its failure without changing the independent observer.");
            File.Delete(optics);
            await KeyPress(Key.P, ctrl: true);
            Require(status.Text.StartsWith("No Earth panoramas", StringComparison.Ordinal), "An empty panorama directory must explain its absence.");
            Directory.CreateDirectory(Path.Combine(root, "broken")); File.WriteAllText(Path.Combine(root, "broken", "landscape.ini"), "bad");
            Directory.CreateDirectory(Path.Combine(root, "large"));
            using (var large = File.Create(Path.Combine(root, "large", "landscape.ini"))) large.SetLength(65537);
            await KeyPress(Key.P, ctrl: true);
            Require(status.Text.StartsWith("No Earth panoramas", StringComparison.Ordinal), "Malformed and oversized definitions must not appear as available panoramas.");
            _scene.SetLandscapeChoice(0, "recoverable");
            await Until(() => status.Text.StartsWith("Landscape recoverable is unavailable.", StringComparison.Ordinal), "Reporting a missing panorama");
            Require(_scene.Renderer(0).VisibleLandscape is null && Session[1] == other,
                "A failed panorama must preserve the ground sky and the independent camera.");
            string recovery = Path.Combine(root, "recoverable"); Directory.CreateDirectory(recovery);
            File.WriteAllText(Path.Combine(recovery, "landscape.ini"), "[landscape]\nname=Recovery\ntype=spherical\nmaptex=ground.png\n[location]\nplanet=Earth\n");
            using (var image = Image.CreateEmpty(4, 2, false, Image.Format.Rgba8))
            {
                image.Fill(new(.2f, .3f, .4f));
                Require(image.SavePng(Path.Combine(recovery, "ground.png")) == Error.Ok, "The repaired local panorama must save.");
            }
            _scene.SetLandscapeChoice(0, "recoverable");
            await Until(() => _scene.Renderer(0).VisibleLandscape is not null, "Retrying a repaired panorama");
            Require(Session[1] == other, "Retrying a repaired panorama must leave the other camera unchanged.");
            await KeyPress(Key.P, ctrl: true, alt: true);
            Require(_scene.Renderer(0).VisibleLandscape is null, "Clearing the repaired panorama must release it from the active view.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_LANDSCAPES", oldLandscapes);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_OPTICS_FILE", oldOptics);
            Require(_scene.LoadScenario(saved), "Restore both observers after optional resource failures.");
        }
        GD.Print("UI: panorama and eyepiece mode refusals, damaged optical library, malformed panoramas and repaired-image retry passed.");
    }
}
