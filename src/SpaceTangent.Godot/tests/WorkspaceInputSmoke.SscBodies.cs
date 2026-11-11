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
