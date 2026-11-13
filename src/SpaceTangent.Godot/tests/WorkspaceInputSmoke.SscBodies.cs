// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseSscBodies(string directory, string original)
    {
        const int view = 0;
        ObjectId station = new("ssc/sol/earth/iss");
        ObjectId asteroid = new("ssc/sol/2008-tc3");
        string local = Path.Combine(directory, "Original SSC with spaces");
        CopyDirectory(original, local);
        var addOns = Find<Button>("AddOnsTab");
        if (!addOns.ButtonPressed)
            await Click(addOns);
        var kind = Find<OptionButton>("LocalAddonKind");
        await Click(kind);
        await ChooseMenuItem(kind.GetPopup(), kind.GetItemIndex((int)LocalAddonKind.BodyCatalog));
        var dialog = _scene.GetNode<FileDialog>("OpenAddonFolder");
        dialog.CurrentDir = local;
        await Click(Find<Button>("BrowseLocalAddon"));
        await Until(() => dialog.Visible, "Opening the SSC source chooser");
        await Click(dialog.GetOkButton());
        await WaitForAddOns("Installing the original body catalogue");
        Require(Session.World.SscBodies.Count == 2, "The pack must provide the original ISS and one real sampled arc.");

        await FlyTo("International Space Station", station, 2460634.5);
        var node = _scene.Renderer(view).ImportedBodyNode(station);
        Require(node is { Visible: true, Mesh: ArrayMesh model } &&
            Enumerable.Range(0, model.GetSurfaceCount()).Any(index =>
                model.SurfaceGetMaterial(index) is StandardMaterial3D { AlbedoTexture: not null }),
            "The original ISS must use its published CMOD geometry and diffuse textures.");
        var accepted = Session.CaptureScenario(view);
        var world = Session.World;
        string texture = Path.Combine(local, "models", "textures", "iss_03.png");
        File.Move(texture, texture + ".missing");
        try
        {
            if (!addOns.ButtonPressed)
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await Until(() => _scene.LastAddonDiagnostic is not null, "Reporting the missing CMOD texture", 20);
            Require(_scene.LastAddonDiagnostic?.Stage == AddonStage.Texture && ReferenceEquals(world, Session.World),
                "A failed body-catalogue reload must retain the last valid world and identify the missing texture.");
            RequireScenario(accepted, Session.CaptureScenario(view), "A rejected CMOD reload");
        }
        finally
        {
            File.Move(texture + ".missing", texture);
        }
        await Click(Find<Button>("ReloadAddOns"));
        await WaitForAddOns("Restoring the original station texture");
        RequireScenario(accepted, Session.CaptureScenario(view), "Reloading the restored SSC source");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ISS_SCENE") is { Length: > 0 } output)
            ScenarioFile.Save(output, accepted);
        string trajectory = Path.Combine(local, "data", "2008TC3.xyzv");
        byte[] originalSamples = File.ReadAllBytes(trajectory);
        try
        {
            File.WriteAllText(trajectory, "2451545 1 2");
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Reloading with a damaged asteroid trajectory");
            Require(Session.World.SscBodies.Select(body => body.Id).SequenceEqual([station]),
                "The damaged asteroid trajectory must be skipped while keeping the original ISS.");
            RequireScenario(accepted, Session.CaptureScenario(view), "Keeping both observers and ΔT during partial SSC reload");
        }
        finally
        {
            File.WriteAllBytes(trajectory, originalSamples);
        }
        await Click(Find<Button>("ReloadAddOns"));
        await WaitForAddOns("Restoring the original asteroid trajectory");
        Require(Session.World.SscBodies.Any(body => body.Id == asteroid),
            "Restoring the original file must make the asteroid available again after reload.");
        GD.Print("UI: isolated the damaged original 2008 TC3 trajectory, kept ISS, both views and ΔT, and restored the asteroid.");
        await FlyTo("2008 TC3", asteroid, 2454450.5);
        Require(_scene.Renderer(view).ImportedBodyNode(asteroid) is { Visible: true },
            "The source trajectory must be available for the imported asteroid.");
        var tracked = Session[view].Navigation;
        await Click(Find<Button>("AdvanceHour0"));
        Require(Session[view].Navigation is { Tracking: true } moved && moved.AnchorKilometers != tracked!.Value.AnchorKilometers,
            "The asteroid camera must follow the real interpolated positions.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TC3_SCENE") is { Length: > 0 } sample)
            ScenarioFile.Save(sample, Session.CaptureScenario(view));
        GD.Print("UI: installed original ISS CMOD and 2008 TC3 XYZV, tracked both, and recovered a missing original texture");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MPC_SELECTION") is { Length: > 0 } mpc)
        {
            string selection = Path.Combine(local, "original-selection.mpc");
            File.Copy(mpc, selection);
            if (!addOns.ButtonPressed)
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Discovering the new local MPC selection");
            Require(Session.World.SscBodies.Count == 8, "Six MPC records must be added alongside the original SSC bodies.");
            await FlyTo("(99942) Apophis", new("ssc/sol/apophis"), 2461200.5);
            await FlyTo("Halley", new("ssc/sol/1p/halley"), 2474037.0655);
            var atlas = new ObjectId("ssc/sol/3i/atlas");
            await FlyTo("3I/ATLAS", atlas, 2460977.9825);
            Require(_scene.Renderer(view).ImportedBodyNode(atlas) is { Visible: true } &&
                Session.World.Catalogue.Find(atlas)?.Kind == CatalogueKind.Comet,
                "The open-orbit comet must be classified and rendered.");
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MPC_SCENE") is { Length: > 0 } mpcScene)
                ScenarioFile.Save(mpcScene, Session.CaptureScenario(view));
            File.Delete(selection);
            if (!addOns.ButtonPressed)
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Removing the local MPC selection");
            Require(Session.World.SscBodies.Count == 2 && Session.World.Catalogue.Find(atlas) is null,
                "Removing the MPC file must invalidate its cached bodies without removing the original SSC objects.");
            GD.Print("UI: added real MPCORB and CometEls records by reload, flew to Apophis, Halley and 3I/ATLAS, then removed the local selection");
        }

        async Task FlyTo(string name, ObjectId id, double julianDay)
        {
            _scene.Seek(view, JulianInstant.FromJulianDate(julianDay, TimeScale.Tdb));
            await Click(Find<SubViewportContainer>("ViewCanvas0"));
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", name);
            await Click(Result(id));
            await Click(Find<Button>("GoToObject"));
            Require(Session.IsFlying(view), $"Flight to the SSC target {name} must start.");
            await Until(() => !Session.IsFlying(view), $"Flying to the SSC target {name}");
            await Click(Find<Button>("TrackObject"));
            Require(Session[view].Navigation is { Tracking: true, Target: { } target } && target == id,
                $"The camera must follow {name}.");
        }
    }
}
