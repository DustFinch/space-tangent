// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExercisePlanetNavigation(string directory)
    {
        if (Session.ViewCount == 1)
            await KeyPress(Key.U, ctrl: true);
        Require(Session.ViewCount == 2, "The planetary regression needs two views.");
        if (Session.Synchronized)
        {
            var time = Find<Button>("TimeTab");
            if (!time.ButtonPressed)
                await Click(time);
            await Click(Find<CheckButton>("SyncViewTime"));
        }
        await FlyTo(0, "Venus", new("sol/venus"));
        await FlyTo(1, "Neptune", new("sol/neptune"));
        var before = Session[1].Navigation;
        ObserverView left = Session[0];
        await Click(Find<Button>("AdvanceHour1"));
        Require(Session[0] == left && Session[1].Navigation is { Tracking: true } after &&
            after.AnchorKilometers != before!.Value.AnchorKilometers,
            "Tracking the outer planet must update its inertial position while preserving the inner view.");

        const string file = "venus-neptune.stscene";
        var expected = Session.CaptureScenario(1);
        await UseScenarioDialog("SaveScenario", 1, directory, file);
        RequireScenario(expected, ScenarioFile.Load(Path.Combine(directory, file)), "Saving both planetary cameras");
        await Click(Find<Button>("SplitViews"));
        await UseScenarioDialog("OpenScenario", 0, directory, file);
        RequireScenario(expected, Session.CaptureScenario(1), "Reopening both planetary cameras");
        Require(_scene.Renderer(0).PlanetGlobeTextured(new("sol/venus")) &&
            _scene.Renderer(1).PlanetGlobeTextured(new("sol/neptune")),
            "Both textured targets must be visible when reopening the scenario.");
        GD.Print("UI: flew to Venus and Neptune, tracked them at different epochs, and restored both cameras from disk");

        const string environment = "SPACETANGENT_PLANET_TEXTURES";
        string? previous = System.Environment.GetEnvironmentVariable(environment);
        _scene.SetMode(0, CameraMode.MarsTerrain);
        Require(_scene.Renderer(0).MarsTerrain is { Visible: true }, "The Olympus Mons preset must show relief.");
        try
        {
            string emptyPack = Path.Combine(directory, "empty-planet-pack");
            Directory.CreateDirectory(emptyPack);
            System.Environment.SetEnvironmentVariable(environment, emptyPack);
            Require(await _scene.ReloadAddOns() && !Session.World.HasMarsTerrain && Session[0].Mode == CameraMode.Solar &&
                _scene.Renderer(0).MarsTerrain is { Visible: false },
                "Losing the optional Mars grid must disable the preset and leave a usable overview.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(environment, previous);
        }
        Require(await _scene.ReloadAddOns() && Session.World.HasMarsTerrain, "Restoring the Mars pack must republish the preset.");
        _scene.SetMode(0, CameraMode.MarsTerrain);
        Require(_scene.Renderer(0).MarsTerrain is { Visible: true }, "Reloaded relief must be visible on reentering Olympus Mons.");
        GD.Print("UI: missing local Mars relief fell back to the overview and was restored by reloading the pack");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_NATURAL_BODIES_SMOKE") == "1")
        {
            await FlyTo(0, "Titan", new("sol/titan"));
            _scene.Seek(1, JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb));
            await FlyTo(1, "Vesta", new("sol/vesta"));
            var beforeStep = Session[1].Navigation;
            await Click(Find<Button>("AdvanceHour1"));
            Require(Session[1].Navigation is { Tracking: true } moved && moved.AnchorKilometers != beforeStep!.Value.AnchorKilometers,
                "Tracking a sampled minor body must advance its interpolated state.");
            var natural = Session.CaptureScenario(1);
            const string naturalScene = "titan-vesta.stscene";
            await UseScenarioDialog("SaveScenario", 1, directory, naturalScene);
            RequireScenario(natural, ScenarioFile.Load(Path.Combine(directory, naturalScene)), "Saving a moon and a sampled minor body");
            await UseScenarioDialog("OpenScenario", 0, directory, naturalScene);
            RequireScenario(natural, Session.CaptureScenario(1), "Reopening a moon and a sampled minor body");
            GD.Print("UI: tracked Titan and Vesta in independent views and restored both from disk");
        }

        async Task FlyTo(int index, string name, ObjectId id)
        {
            await Click(Find<SubViewportContainer>($"ViewCanvas{index}"));
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", name);
            await Click(Result(id));
            await Click(Find<Button>("GoToObject"));
            Require(Session.IsFlying(index), $"The search result for {name} must start a flight.");
            await Until(() => !Session.IsFlying(index), $"Flying to {name}");
            Require(Session[index].Navigation?.Target == id && _scene.Renderer(index).PlanetGlobeTextured(id),
                $"The flight must reach the textured body for {name}.");
            await Click(Find<Button>("TrackObject"));
            Require(Session[index].Navigation is { Tracking: true, Target: { } target } && target == id,
                $"The camera must track {name}.");
        }
    }
}
