// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExercisePlanets(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        var world = scene.Session!.World;
        CheckPlanetCenters(world);
        var snapshot = world.Graph.Evaluate(epoch);
        var solar = snapshot.SolarSystem!.Value;
        bool testRingPack = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_RING_TEXTURES"));
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (planet.NaifId == 399)
                continue;
            if (!snapshot.TryState(planet.Id, out CartesianState state) ||
                !snapshot.TryOrientation(planet.Id, out var rotation))
                throw new InvalidOperationException($"{planet.Name} needs both a DE440 state and a PCK orientation.");
            Vector3 sun = Axis((solar.Sun - state).PositionKilometers).Normalized();
            Vector3 pole = Axis(rotation.ToWorld.Z).Normalized();
            Vector3 east = sun.Cross(pole).Normalized();
            bool saturn = planet.NaifId == 699;
            bool ringsExpected = saturn || (testRingPack && PlanetRingRendering.Profiles.Any(profile => profile.Id == planet.Id));
            Vector3 direction = (sun + pole * (saturn ? .78f : .22f) + east * .32f).Normalized();
            await CapturePlanet(planet.Name.ToLowerInvariant(), direction, saturn ? 7 : 4.2);
            if (ringsExpected && !saturn)
                await CapturePlanet(planet.Name.ToLowerInvariant() + "-rings", (east + pole * .85f + sun * .35f).Normalized(), 6.3);
            if (saturn)
            {
                var nearPlane = (sun - pole * sun.Dot(pole) + pole * .04f + east * .35f).Normalized();
                await CapturePlanet("saturn-edge", nearPlane, 7);
                await CapturePlanet("saturn-ring-shadows", (east + pole * .78f + sun * .35f).Normalized(), 6.3);
            }

            async Task CapturePlanet(string name, Vector3 direction, double range)
            {
                var pose = new NavigationCamera(state.PositionKilometers, planet.RadiusKilometers * range,
                    Math.Atan2(direction.X, direction.Z), Math.Asin(direction.Y), planet.Id, true);
                await AcceptView(name, new(epoch, CameraMode.Solar, planet.Id, false) { Navigation = pose });
                if (!scene.Renderer(0).PlanetGlobeTextured(planet.Id) || (ringsExpected && !scene.Renderer(0).PlanetRingsVisible(planet.Id)))
                    throw new InvalidOperationException($"Planetary data are missing for {name}.");
            }
        }
        await AcceptView("inner-planets", new(epoch, CameraMode.Solar, null, false));
        await AcceptView("outer-planets", new(epoch, CameraMode.SolarOuter, null, false));
        if (scene.Renderer(0).PlanetOrbitCount != 4)
            throw new InvalidOperationException("The outer overview must contain four orbit paths.");

        if (!world.HasMarsTerrain)
            throw new InvalidOperationException("The planetary probe needs the Mars relief dataset.");
        for (int hour = 0; hour < 25; hour++)
        {
            JulianInstant localEpoch = epoch.AddSeconds(3600 * hour);
            var local = world.Graph.Evaluate(localEpoch);
            if (!local.TryState(PlanetCatalogue.MarsId, out CartesianState mars) ||
                !local.TryOrientation(PlanetCatalogue.MarsId, out var frame))
                continue;
            Vector3d direction = (local.SolarSystem!.Value.Sun - mars).PositionKilometers;
            Vector3d sun = frame.ToWorld.Transpose().Transform(direction);
            const double radians = Math.PI / 180;
            var (sinLon, cosLon) = Math.SinCos(-133.8 * radians);
            var (sinLat, cosLat) = Math.SinCos(18.65 * radians);
            Vector3d zenith = new(cosLat * cosLon, cosLat * sinLon, sinLat);
            double elevationSine = Vector3d.Dot(sun, zenith) / sun.Length;
            if (elevationSine is < .28 or > .55)
                continue;
            await AcceptView("mars-olympus-mons", new(localEpoch, CameraMode.MarsTerrain, PlanetCatalogue.MarsId, false));
            if (scene.Renderer(0).MarsTerrain is not { Visible: true, Regional: true })
                throw new InvalidOperationException("The local Olympus Mons terrain mesh must be visible.");
            return;
        }
        throw new InvalidOperationException("No suitably lit Olympus Mons frame was found.");

        async Task AcceptView(string name, ObserverView observer)
        {
            string file = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(file, new([observer], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(file))
                throw new InvalidOperationException($"Could not reopen the planetary scenario {name}.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                    throw new IOException($"Could not capture {name}.");
            }
            GD.Print($"Planetary camera restored: {name}.");
        }

        static Vector3 Axis(Vector3d value) => new((float)value.X, (float)value.Z, (float)-value.Y);
    }
}
