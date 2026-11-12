// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseSurfaceSites(string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_LOCATIONS")))
            return;
        var search = Find<Button>("SearchTab");
        if (!search.ButtonPressed)
            await Click(search);
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        var epoch = new JulianInstant(2460600, 0, TimeScale.Tdb);
        _scene.Seek(0, epoch);
        Session.SetPlaying(0, false);
        await KeyPress(Key.Enter);
        var query = Find<LineEdit>("ObjectQuery");
        Require(query.HasFocus(), "The search shortcut must focus the existing object field.");
        await KeyPress(Key.A, ctrl: true);
        await TypeText("Olympus Mons");
        Require(query.Text == "Olympus Mons", "The search field must receive the location name.");
        ObjectId olympus = new("location/sol/mars/olympus-mons");
        _ = Find<Container>("ObjectResults").FindChildren("*", "Button", recursive: true, owned: false)
            .OfType<Button>().Single(button => button.TooltipText == olympus.Value);
        await KeyPress(Key.Enter);
        Require(Session[0].Selection == olympus, "The original Olympus Mons entry must be searchable.");
        await KeyPress(Key.G);
        Require(Session.IsFlying(0), "The location must use the normal fly-to command.");
        await Until(() => !Session.IsFlying(0), "Flying to Olympus Mons");
        var atSite = Session[0].Navigation!.Value;
        Require(atSite is { Tracking: true, Placement: { Frame: NavigationFrame.Surface, Reference: { } body } } &&
            body == PlanetCatalogue.MarsId && _scene.Renderer(0).SiteLabelVisible(olympus),
            "The fly-to must arrive above the site with a visible label and a Mars-fixed camera.");
        var before = Session.World.Graph.Evaluate(epoch);
        _scene.Advance(0, 3600);
        var later = Session.World.Graph.Evaluate(Session[0].Epoch);
        var moved = Session[0].Navigation!.Value;
        Require(moved.Placement == atSite.Placement && moved.AnchorKilometers != atSite.AnchorKilometers &&
            Vector3d.Dot(moved.ResolvePlacement(later, Session.DeltaT).Forward,
                atSite.ResolvePlacement(before, Session.DeltaT).Forward) < .999,
            "Advancing time must rotate the site camera with Mars.");
        var expected = Session.CaptureScenario(0);
        Require(await _scene.ReloadAddOns(), "A world with surface locations must reload.");
        RequireScenario(expected, Session.CaptureScenario(0), "Reloading the named surface observer");

        var tychoId = new ObjectId("location/sol/moon/tycho");
        var tycho = Session.World.FindSurfaceSite(tychoId)!;
        var graph = Session.World.Graph.Evaluate(epoch);
        Require(Session.World.TrySurfaceFrame(tycho, graph, Session.DeltaT, out var moon, out var rotation),
            "The original Tycho record requires a Moon-fixed frame.");
        Vector3d radial = rotation.Transform(tycho.Radial);
        Vector3d up = rotation.Transform(tycho.North);
        foreach (bool near in new[] { true, false })
        {
            Vector3d forward = radial * (near ? -1 : 1);
            var camera = new NavigationCamera(moon.PositionKilometers, 6700, 0, 0, null, false)
                { Placement = new(forward * -6700, forward, up) };
            var observer = new ObserverView(epoch, CameraMode.Solar, tychoId, false) { Navigation = camera };
            string file = Path.Combine(directory, near ? "tycho-near.stscene" : "tycho-far.stscene");
            ScenarioFile.Save(file, new([observer], true, 0, 0, Session.DeltaT));
            Require(_scene.LoadScenario(file) && _scene.Renderer(0).SiteLabelVisible(tychoId) == near,
                "The location label must follow the Moon and disappear behind its limb.");
        }
        GD.Print("UI: found and flew to original Olympus Mons, reloaded its rotating view, and hid Tycho on the far side");
    }
}
