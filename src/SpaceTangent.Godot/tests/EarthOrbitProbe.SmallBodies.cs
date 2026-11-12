// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSmallBodies(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        SimulationWorld world = scene.Session!.World;
        CheckMoonStates(world);
        var snapshot = world.Graph.Evaluate(epoch);
        var later = world.Graph.Evaluate(epoch.AddSeconds(86400 * 3));
        var solar = snapshot.SolarSystem!.Value;
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            if (world.Catalogue.Search(body.Name).FirstOrDefault()?.Id != body.Id ||
                world.Catalogue.Find(body.Id)?.Parent != body.Parent ||
                !snapshot.TryState(body.Id, out CartesianState state) ||
                !snapshot.TryState(body.Parent, out CartesianState parent) ||
                !later.TryState(body.Id, out CartesianState next) ||
                !later.TryState(body.Parent, out CartesianState nextParent))
                throw new InvalidOperationException($"The object search, hierarchy or state is missing for {body.Name}.");
            double distance = (state - parent).PositionKilometers.Length;
            double moved = ((next - nextParent) - (state - parent)).PositionKilometers.Length;
            double maximumMoonDistance = body.NaifId switch { 609 => 15_100_000, 802 => 9_700_000, _ => 4_000_000 };
            if (!(distance > body.RadiusKilometers && moved > 0.05) ||
                (body.Kind == CatalogueKind.Moon && distance > maximumMoonDistance))
                throw new InvalidOperationException($"Invalid parent-relative distance or motion for {body.Name}.");
        }

        ObjectId jupiter = new("sol/jupiter");
        if (!snapshot.TryOrientation(jupiter, out var frame))
            throw new InvalidOperationException("The Jupiter moon scene needs the rotational pole.");
        await Capture("jupiter-moons", jupiter, Axis(frame.ToWorld.Z).Normalized(), 5_500_000);
        foreach (NaturalBodyDefinition moon in NaturalBodyCatalogue.Bodies.Where(body => body.Parent == jupiter))
            if (!scene.Renderer(0).PlanetGlobeTextured(moon.Id))
                throw new InvalidOperationException($"The Jupiter scene must contain a marker for {moon.Name}.");

        foreach (var (name, id, range) in new (string, ObjectId, double)[]
                 { ("titan", new("sol/titan"), 12_000), ("enceladus", new("sol/enceladus"), 1400),
                   ("miranda", new("sol/miranda"), 1300), ("triton", new("sol/triton"), 7300), ("pluto", NaturalBodyCatalogue.PlutoId, 5600),
                   ("ceres", new("sol/ceres"), 2200), ("vesta", new("sol/vesta"), 1400) })
        {
            snapshot.TryState(id, out CartesianState body);
            Vector3 sun = Axis((solar.Sun - body).PositionKilometers).Normalized();
            Vector3 direction = (sun + sun.Cross(Vector3.Up).Normalized() * .27f).Normalized();
            await Capture(name, id, direction, range);
            if (!scene.Renderer(0).PlanetGlobeTextured(id))
                throw new InvalidOperationException($"The {name} texture must be visible.");
        }

        var comet = NaturalBodyCatalogue.CometId;
        var activityEpoch = JulianInstant.FromJulianDate(2459526.5, TimeScale.Tdb);
        await CheckActivity("comet-67p", comet, activityEpoch);
        await CheckActivity("comet-atlas", new("ssc/sol/3i/atlas"),
            JulianInstant.FromJulianDate(2460977.9825, TimeScale.Tdb), required: false);
        await Capture("comet-67p-inactive", comet, new Vector3(.3f, .2f, .93f).Normalized(), 60);
        if (scene.Renderer(0).RenderedComet(comet) is not null)
            throw new InvalidOperationException("A distant comet must have no visible activity.");

        async Task CheckActivity(string name, ObjectId id, JulianInstant at, bool required = true)
        {
            var active = world.Graph.Evaluate(at);
            if (!active.TryState(id, out var nucleus))
            {
                if (required)
                    throw new InvalidOperationException($"The original state for {name} is missing.");
                return;
            }
            var relative = nucleus - active.SolarSystem!.Value.Sun;
            Vector3d away = relative.PositionKilometers / relative.PositionKilometers.Length;
            Vector3d motion = relative.VelocityKilometersPerSecond;
            Vector3d retrograde = away * Vector3d.Dot(motion, away) - motion;
            retrograde /= retrograde.Length;
            Vector3 eye = Axis(Vector3d.Cross(away, retrograde)).Normalized();
            double length = 1e15 / relative.PositionKilometers.Length * (id == comet ? .4 : .2);
            await Capture(name, id, eye, length * 1.35, away * (length * .55), at);
            if (scene.Renderer(0).RenderedComet(id) is not { } state ||
                Vector3d.Dot(state.Away, away) < .999999999 || Vector3d.Dot(state.Trail, retrograde) < .999999999 ||
                Math.Abs(state.Length - length) > .001 || state.ComaFade < .999)
                throw new InvalidOperationException($"The ion and dust directions or coma visibility disagree for {name}.");
            await Capture(name + "-nucleus", id, eye, 60, at: at);
            if (scene.Renderer(0).RenderedComet(id) is not { ComaFade: 0 })
                throw new InvalidOperationException("The coma must fade when the camera is inside it.");
            GD.Print($"Comet activity: {name} anti-solar ions, trailing dust, coma and inner-camera fade.");
        }

        async Task Capture(string name, ObjectId id, Vector3 direction, double range, Vector3d offset = default,
            JulianInstant? at = null)
        {
            var frame = at is { } date ? world.Graph.Evaluate(date) : snapshot;
            if (!frame.TryState(id, out CartesianState state))
                throw new InvalidOperationException($"Missing state for {name}.");
            var pose = new NavigationCamera(state.PositionKilometers + offset, range, Math.Atan2(direction.X, direction.Z),
                Math.Clamp(Math.Asin(direction.Y), -1.55, 1.55), id, offset == default);
            var view = new ObserverView(at ?? epoch, CameraMode.Solar, id, false) { Navigation = pose };
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([view], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not reopen {name}.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                    throw new IOException($"Could not capture {name}.");
            }
            GD.Print($"Natural body camera restored: {name}.");
        }

        static Vector3 Axis(Vector3d value) => new((float)value.X, (float)value.Z, (float)-value.Y);
    }
}
