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
        bool stc = System.Environment.GetEnvironmentVariable("SPACETANGENT_STC_ROOT") is { Length: > 0 };
        int baseCount = archive ? 182_625 : 109_400;
        if ((stc ? world.Stellar.Stars.Count <= baseCount : world.Stellar.Stars.Count != baseCount) || !world.TryNavigationState(star, snapshot, out var proxima))
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
        if (stc)
        {
            var center = world.Stellar.Find(new("hip/116727"))!;
            await Capture("errai-system", center.Id, center.StateAt(epoch).PositionKilometers + world.StellarOrigin,
                center.SystemRadiusKilometers * 2.7, sky);
            if (scene.Renderer(0).VisibleStellarDisks != 2 || scene.Renderer(0).StellarDiskResolved)
                throw new InvalidOperationException("The original STC Errai system must render both components.");
        }
        if (world.Exoplanets.Bodies.Count > 0)
        {
            var exoplanet = world.Exoplanets.Find(new("exo/113357/dimidium"))!;
            if (!world.TryNavigationState(exoplanet.Id, snapshot, out var state) ||
                !world.TryNavigationState(exoplanet.Host, snapshot, out var host))
                throw new InvalidOperationException("The original Dimidium system must have orbital states.");
            Vector3d light = (host - state).PositionKilometers;
            light /= light.Length;
            Vector3d eye = light + Vector3d.Cross(light, new(0, 0, 1)) * .42;
            await Capture("dimidium", exoplanet.Id, state.PositionKilometers, exoplanet.RadiusKilometers * 4.2, sky, eye / eye.Length);
            if (!scene.Renderer(0).ExoplanetVisible(exoplanet.Id) || !scene.Renderer(0).ExoplanetTextured(exoplanet.Id))
                throw new InvalidOperationException("The original Dimidium map must be visible in the planet view.");
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

        async Task Capture(string name, ObjectId? id, Vector3d center, double range, SkyLayerSettings layers, Vector3d? eye = null)
        {
            double azimuth = eye is { } direction ? Math.Atan2(direction.X, -direction.Y) : -.7;
            double elevation = eye is { } offset ? Math.Asin(offset.Z) : .18;
            var pose = new NavigationCamera(center, range, azimuth, elevation, id, id.HasValue);
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
