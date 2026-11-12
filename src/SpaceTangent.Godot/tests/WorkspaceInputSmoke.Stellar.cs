// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseSpatialSky(string directory)
    {
        await FlyTo(0, "Proxima Centauri", new("hip/70890"));
        await FlyTo(1, "Andromeda Galaxy", new("dso/m31"));
        Require(_scene.Renderer(0).StellarDiskResolved && _scene.Renderer(1).DeepSkyVisible(new("dso/m31")),
            "The two views must render a nearby star and a galaxy separately.");
        var expected = Session.CaptureScenario(1);
        const string filename = "star-and-galaxy.stscene";
        await UseScenarioDialog("SaveScenario", 1, directory, filename);
        RequireScenario(expected, ScenarioFile.Load(Path.Combine(directory, filename)), "Saving interstellar targets");
        await UseScenarioDialog("OpenScenario", 0, directory, filename);
        RequireScenario(expected, Session.CaptureScenario(1), "Reopening interstellar targets");
        NavigationCamera distant = expected.Views[1].Navigation!.Value;
        Vector3d anchor = distant.AnchorKilometers;
        var shifted = distant with { AnchorKilometers = new(Math.BitIncrement(anchor.X), anchor.Y, anchor.Z) };
        var fixture = expected with { Views = [expected.Views[0], expected.Views[1] with { Navigation = shifted }] };
        string shiftedScene = Path.Combine(directory, "exact-distant-anchor.stscene");
        ScenarioFile.Save(shiftedScene, fixture);
        Require(_scene.LoadScenario(shiftedScene), "The distant tracking fixture must open.");
        RequireScenario(fixture, Session.CaptureScenario(1), "Restoring the exact distant anchor");
        await Click(Find<Button>("AdvanceHour1"));
        Require(Session[1].Navigation?.AnchorKilometers == anchor,
            "Advancing time must resume tracking the catalogue position.");
        GD.Print("UI: flew to Proxima Centauri and Andromeda, then saved and restored the two interstellar cameras exactly");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT") is { Length: > 0 })
        {
            Require(Session.World.DeepSky.Objects.Count == 17_610, "All original DSC objects and the built-in nebula must be available.");
            var m13 = new ObjectId("dso/m13");
            await FlyTo(1, "NGC 6205", m13);
            var renderer = _scene.Renderer(1);
            Require(renderer.DeepSkyVisible(m13) && Session.World.Catalogue.Find(m13)?.Kind == CatalogueKind.Cluster,
                "The original globular cluster must be searchable and drawn.");
            Vector2 center = renderer.GetViewport().GetVisibleRect().GetCenter();
            Require(renderer.PickObject(center) == m13, "The visible original cluster must be selectable in the frame.");
            var accepted = Session.CaptureScenario(1);
            Require(await _scene.ReloadAddOns(), "The original DSC catalogues must reload.");
            RequireScenario(accepted, Session.CaptureScenario(1), "Reloading the original globular cluster");
            GD.Print("UI: installed all 17,609 original DSC records, searched by NGC 6205, flew to and picked M13, and reloaded the two views");
        }

        async Task FlyTo(int view, string query, ObjectId target)
        {
            await Click(Find<SubViewportContainer>($"ViewCanvas{view}"));
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", query);
            await Click(Result(target));
            Require(Find<Button>("GoToObject").Text == "Go to", $"The {query} card must offer space flight.");
            await Click(Find<Button>("GoToObject"));
            Require(Session.IsFlying(view), $"Flight to {query} must start.");
            await Until(() => !Session.IsFlying(view), $"Flying to {query}");
            Require(Session[view].Navigation?.Target == target, $"The camera must arrive at {query}.");
            await Click(Find<Button>("TrackObject"));
            Require(Session[view].Navigation is { Tracking: true }, $"Tracking {query} must be available.");
        }
    }
}
