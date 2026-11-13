// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task CaptureSurfaceSite(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_LOCATIONS")) ||
            DisplayServer.GetName() == "headless")
            return;
        var session = scene.Session!;
        var olympus = new ObjectId("location/sol/mars/olympus-mons");
        var site = session.World.FindSurfaceSite(olympus) ?? throw new InvalidOperationException("The real Olympus Mons catalogue entry is missing.");
        var snapshot = session.World.Graph.Evaluate(epoch);
        var deltaT = DeltaTModel.MorrisonStephenson2004;
        if (!session.World.TryNavigationState(olympus, snapshot, out CartesianState state, deltaT))
            throw new InvalidOperationException("The original site needs a Mars-fixed state.");
        const double range = 1470;
        var camera = new NavigationCamera(state.PositionKilometers, range, 0, 0, olympus, true)
        {
            Placement = new(site.Radial * range, site.Radial * -1, site.North) { Frame = NavigationFrame.Surface, Reference = site.Body }
        };
        var view = new ObserverView(epoch, CameraMode.Solar, olympus, false) { Navigation = camera };
        string path = Path.Combine(directory, "surface-location-olympus.stscene");
        ScenarioFile.Save(path, new([view], true, 0, 0, deltaT));
        if (!scene.LoadScenario(path) || !scene.Renderer(0).SiteLabelVisible(olympus))
            throw new InvalidOperationException("The Olympus Mons scene did not show its surface label.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.Combine(directory, "surface-location-olympus.png")) != Error.Ok)
            throw new IOException("Could not capture the Olympus Mons view.");
        GD.Print("Surface location rendered: Olympus Mons with a Mars-fixed camera.");

        var untargetedSite = view with
        {
            Selection = null,
            Navigation = camera with
            {
                Target = site.Body, Tracking = false,
                Placement = camera.ResolvePlacement(snapshot, deltaT)
            }
        };
        var places = untargetedSite with { Sky = untargetedSite.Sky with { SurfacePlaces = true } };
        string layers = Path.Combine(directory, "surface-location-layer.stscene");
        ScenarioFile.Save(layers, new([places, untargetedSite], false, 0, 0, deltaT));
        if (!scene.LoadScenario(layers) || !await scene.ReloadAddOns() ||
            !scene.Renderer(0).SiteLabelVisible(olympus) || scene.Renderer(1).SiteLabelVisible(olympus))
            throw new InvalidOperationException("The Olympus Mons label must obey the independent place layers after reload.");
        GD.Print("Archived place layers GPU: unselected Olympus Mons is visible only with Places enabled.");
    }
}
