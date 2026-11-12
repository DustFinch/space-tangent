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

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE") is { Length: > 0 })
        {
            Require(Session.World.Stellar.Stars.Count >= 182_625, "The source archive must preserve its entire nearby-star catalogue.");
            var sirius = new ObjectId("hip/32349");
            await FlyTo(0, "Dog Star", sirius);
            Require(_scene.Renderer(0).VisibleStellarDisks == 2 && !_scene.Renderer(0).StellarDiskResolved,
                "The visible system must contain two components and no barycenter disk.");
            var world = Session.World;
            var a = world.Stellar.FindByName("Sirius A")!;
            var b = world.Stellar.FindByName("Sirius B")!;
            double distance = (a.StateAt(Session[0].Epoch) - b.StateAt(Session[0].Epoch)).PositionKilometers.Length;
            _scene.Advance(0, 86400 * 180d);
            double next = (a.StateAt(Session[0].Epoch) - b.StateAt(Session[0].Epoch)).PositionKilometers.Length;
            Require(Math.Abs(distance - next) > 1e6 && Session[0].Navigation?.Target == sirius,
                "The tracked binary must move around its own barycenter as time advances.");
            GD.Print("UI: found the archived Sirius barycenter by an original alias, flew to the two components, and advanced their orbit");
        }

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_STC_ROOT") is { Length: > 0 })
        {
            var gamma = new ObjectId("hip/116727");
            await FlyTo(0, "GAM Cep", gamma);
            var world = Session.World;
            Require(world.Stellar.FindByName("GAM Cep A")?.BarycenterId == gamma &&
                world.Stellar.FindByName("GAM Cep B")?.BarycenterId == gamma && _scene.Renderer(0).VisibleStellarDisks == 2,
                "Both original STC components must retain their separate identity and orbit the common center.");
            var accepted = Session.CaptureScenario(0);
            Require(await _scene.ReloadAddOns(), "The original STC sources must reload.");
            RequireScenario(accepted, Session.CaptureScenario(0), "Reloading the original STC binary system");
            GD.Print("UI: loaded the original STC corpus, flew to Gamma Cephei by its alias and retained both components after reload");
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
