// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseStellarSpace(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        SimulationWorld world = scene.Session!.World;
        var snapshot = world.Graph.Evaluate(epoch);
        ObjectId star = new("hip/70890");
        bool archive = System.Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE") is { Length: > 0 };
        if (world.Stellar.Stars.Count != (archive ? 182_625 : 109_400) || !world.TryNavigationState(star, snapshot, out var proxima))
            throw new InvalidOperationException("The spatial catalogue count or Proxima Centauri does not match the verified source.");
        var sky = new SkyLayerSettings(true, false, false, false) { DeepSky = true };
        await Capture("proxima-distant", star, proxima.PositionKilometers, 6e8, sky);
        if (scene.Renderer(0).StellarDiskResolved || !scene.Renderer(0).SpatialStarFieldVisible)
            throw new InvalidOperationException("The distant star must be unresolved against the spatial field.");
        await Capture("proxima-near", star, proxima.PositionKilometers, 450_000, sky);
        if (!scene.Renderer(0).StellarDiskResolved || !scene.Renderer(0).SpatialStarFieldVisible)
            throw new InvalidOperationException("The nearby star must resolve to a disk, with the field behind it.");
        if (archive)
        {
            var center = world.Stellar.FindByName("Dog Star")!;
            await Capture("sirius-system", center.Id, center.StateAt(epoch).PositionKilometers + world.StellarOrigin,
                center.SystemRadiusKilometers * 2.7, sky);
            if (scene.Renderer(0).VisibleStellarDisks != 2 || scene.Renderer(0).StellarDiskResolved)
                throw new InvalidOperationException("The Sirius system must render two components and an invisible barycenter.");
        }
        string[] nearby = world.DeepSky.Objects.Count > DeepSkyCatalogue.Objects.Count
            ? ["dso/m31", "dso/m42", "dso/m45", "dso/m13"] : ["dso/m31", "dso/m42", "dso/m45"];
        foreach (DeepSkyObject body in nearby.Select(id => world.DeepSky.Find(new(id))!))
        {
            var center = body.PositionKilometers + world.StellarOrigin;
            await Capture(body.Designation.ToLowerInvariant(), body.Id, center, body.RadiusKilometers * 4.2, sky);
            if (!scene.Renderer(0).DeepSkyVisible(body.Id))
                throw new InvalidOperationException($"The deep-sky renderer is missing {body.Designation}.");
        }
        DeepSkyObject galaxy = world.DeepSky.Find(new("dso/m31"))!;
        Vector3d galaxyCenter = galaxy.PositionKilometers + world.StellarOrigin;
        await Capture("deep-sky-enabled", null, galaxyCenter, galaxy.RadiusKilometers * 4.2, sky);
        if (!scene.Renderer(0).DeepSkyVisible(galaxy.Id))
            throw new InvalidOperationException("Enabling the deep-sky layer must show untargeted objects.");
        await Capture("deep-sky-disabled", null, galaxyCenter, galaxy.RadiusKilometers * 4.2, default);
        if (scene.Renderer(0).DeepSkyVisible(galaxy.Id))
            throw new InvalidOperationException("Disabling the deep-sky layer must hide untargeted objects.");

        async Task Capture(string name, ObjectId? id, Vector3d center, double range, SkyLayerSettings layers)
        {
            var pose = new NavigationCamera(center, range, -.7, .18, id, id.HasValue);
            var observer = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose, Sky = layers };
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([observer], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not reopen the stellar scenario {name}.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                    throw new IOException($"Could not capture {name}.");
            }
            GD.Print($"Stellar camera restored: {name}.");
        }
    }
}
