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
    private async Task ExercisePlanetPhotometry(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (DisplayServer.GetName() == "headless")
            return;
        var session = scene.Session!;
        var snapshot = session.World.Graph.Evaluate(epoch);
        var solar = snapshot.SolarSystem!.Value;
        var saturn = PlanetCatalogue.Find(PlanetCatalogue.SaturnId)!;
        if (!snapshot.TryState(saturn.Id, out var planet))
            throw new InvalidOperationException("The original Saturn state is required.");
        string path = Path.Combine(directory, "saturn-and-moon-photometry.stscene");
        var near = View(saturn.Id, planet, saturn.RadiusKilometers * 4);
        Save(near, near);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        double focal = scene.Renderer(0).GetViewport().GetVisibleRect().Size.Y / (2 * Math.Tan(49 * Math.PI / 360));
        var far = View(saturn.Id, planet, saturn.RadiusKilometers * (1 + focal / .5));
        Save(far, near);
        if (scene.Renderer(0).BodyPoint(saturn.Id) is not { Visible: true } point ||
            scene.Renderer(0).PlanetRingsVisible(saturn.Id) ||
            scene.Renderer(1).BodyPoint(saturn.Id)?.Visible == true ||
            !scene.Renderer(1).PlanetRingsVisible(saturn.Id))
            throw new InvalidOperationException("Only the unresolved Saturn must use a point. The resolved rings must stay visible.");
        var renderer = scene.Renderer(0);
        if (renderer.PickObject(renderer.GetViewport().GetVisibleRect().Size / 2) != saturn.Id)
            throw new InvalidOperationException("The unresolved Saturn must remain selectable.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var withPoint = renderer.GetViewport().GetTexture().GetImage();
        point.Hide();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var withoutPoint = renderer.GetViewport().GetTexture().GetImage();
        point.Show();
        Vector2I center = withPoint.GetSize() / 2;
        if (withPoint.GetPixel(center.X, center.Y).IsEqualApprox(withoutPoint.GetPixel(center.X, center.Y)))
            throw new InvalidOperationException("The unresolved Saturn must change the center pixel.");

        var moon = SolarSystemSample.MoonId;
        double radius = NavigationCamera.BodyRadiusKilometers(moon);
        Save(far, View(moon, solar.Moon, radius * (1 + focal / .5)));
        if (scene.Renderer(1).BodyPoint(moon) is not { Visible: true } ||
            scene.Renderer(0).BodyPoint(saturn.Id) is not { Visible: true })
            throw new InvalidOperationException("Saturn and the Moon must render independently as points.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not save the unresolved Saturn and Moon.");
        GD.Print("Planet photometry GPU: unresolved Saturn and Moon, selectable point, resolved rings in the independent view.");
        foreach (var (leftId, rightId) in new (ObjectId, ObjectId)[]
                 { (NaturalBodyCatalogue.PlutoId, new("sol/ceres")), (new("sol/vesta"), NaturalBodyCatalogue.CometId) })
        {
            ObserverView left = SmallBodyView(leftId), right = SmallBodyView(rightId);
            Save(left, right);
            if (scene.Renderer(0).BodyPoint(leftId) is not { Visible: true } leftPoint ||
                scene.Renderer(1).BodyPoint(rightId) is not { Visible: true } rightPoint ||
                scene.Renderer(0).PlanetGlobeTextured(leftId) || scene.Renderer(1).PlanetGlobeTextured(rightId))
                throw new InvalidOperationException($"The original {leftId} and {rightId} must become unresolved points.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var rendered = GetViewport().GetTexture().GetImage();
            if (rendered.SavePng(Path.Combine(directory, $"small-body-points-{leftId.Value.Split('/')[^1]}.png")) != Error.Ok)
                throw new IOException("Could not save the original unresolved small bodies.");
            foreach (var (index, id, node) in new[] { (0, leftId, leftPoint), (1, rightId, rightPoint) })
            {
                var view = scene.Renderer(index);
                Vector2 pixel = view.NavigationCameraNode.UnprojectPosition(node.Position);
                if (view.PickObject(pixel) != id)
                    throw new InvalidOperationException($"The unresolved {id} must remain selectable.");
            }
        }
        GD.Print("Small-body photometry GPU: points and picking for Pluto, Ceres, Vesta and comet 67P.");
        var vestaId = new ObjectId("sol/vesta");
        var labelSky = new SkyLayerSettings(false, true, false, false);
        var trackedVesta = SmallBodyView(vestaId) with { Selection = null, Sky = labelSky };
        var labeledSaturn = near with { Sky = labelSky };
        Save(labeledSaturn, trackedVesta);
        if (scene.Renderer(0).BodyLabel(saturn.Id) is not { Visible: true, Text: "Saturn" } ||
            scene.Renderer(1).BodyLabel(vestaId)?.Visible == true)
            throw new InvalidOperationException("The planet label should show while unselected minor bodies are hidden.");
        Save(labeledSaturn, trackedVesta with { Selection = vestaId });
        if (scene.Renderer(1).BodyLabel(vestaId) is not { Visible: true, Text: "Vesta" })
            throw new InvalidOperationException("Selecting the original Vesta must show its minor body label.");
        string profile = Path.Combine(directory, "minor-labels-profile.json");
        File.WriteAllText(profile, """{"Site":{},"Labels":true,"MinorLabels":true}""");
        var importedLabels = LegacyObserverProfiles.Read(profile, trackedVesta, session.DeltaT).Observer;
        Save(labeledSaturn, importedLabels);
        if (scene.Renderer(1).BodyLabel(vestaId) is not { Visible: true } ||
            !session[1].Sky.MinorBodyLabels || session[0].Sky.MinorBodyLabels)
            throw new InvalidOperationException("The archived minor labels preference must apply only to its saved view.");
        GD.Print("Body labels GPU: Saturn, selected Vesta and the archived independent minor labels preference.");
        string? packed = System.Environment.GetEnvironmentVariable("SPACETANGENT_ASTEROIDS_SSC");
        if (!string.IsNullOrEmpty(packed))
        {
            const string input = "SPACETANGENT_SSC_ADDON_ROOT";
            string? previous = System.Environment.GetEnvironmentVariable(input);
            try
            {
                var id = new ObjectId("sol/vesta");
                float? looseMagnitude = null;
                foreach (string source in new[] { packed.Replace("-packed", "", StringComparison.Ordinal), packed })
                {
                    System.Environment.SetEnvironmentVariable(input, source);
                    if (!await scene.ReloadAddOns())
                        throw new InvalidOperationException($"Could not load the original Vesta catalogue: {scene.LastAddonDiagnostic}.");
                    var vesta = session.World.SscBodies.Single(body => body.Id == id);
                    var current = session.World.Graph.Evaluate(epoch);
                    if (vesta.Definition.GeometricAlbedo != .38 || !current.TryState(id, out var asteroid))
                        throw new InvalidOperationException("The original SSC Vesta must keep its albedo and identity.");
                    double vestaRadius = vesta.Definition.RadiusKilometers;
                    double range = vestaRadius * (1 + focal / .7);
                    Save(View(id, asteroid, range), View(id, asteroid, vestaRadius * 4));
                    var left = scene.Renderer(0);
                    if (left.BodyPoint(id) is not { Visible: true, MaterialOverride: ShaderMaterial material } pointNode ||
                        left.ImportedBodyNode(id) is not { Visible: false } ||
                        scene.Renderer(1).ImportedBodyNode(id) is not { Visible: true } ||
                        left.PickObject(left.NavigationCameraNode.UnprojectPosition(pointNode.Position)) != id)
                        throw new InvalidOperationException("Vesta must render as a selectable point and as a nearby SSC model.");
                    Vector3d sun = (solar.Sun - asteroid).PositionKilometers;
                    Vector3d eye = session[0].Navigation!.Value.ResolvePlacement(current, session.DeltaT).OffsetKilometers;
                    double lit = (1 + Vector3d.Dot(sun / sun.Length, eye / eye.Length)) / 2;
                    double ratio = vestaRadius / sun.Length * (149597870.7 / range);
                    double expected = -26.74 - 2.5 * Math.Log10(.38 * lit * ratio * ratio);
                    float actual = material.GetShaderParameter("magnitude").AsSingle();
                    if (Math.Abs(expected - actual) > 1e-4 || (looseMagnitude is { } earlier && earlier != actual))
                        throw new InvalidOperationException("Vesta's source and packed apparent magnitudes must match the original albedo.");
                    looseMagnitude = actual;
                }
                GD.Print($"SSC photometry GPU: original Vesta source and packed point magnitude {looseMagnitude:F5}, selectable point and near model.");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable(input, previous);
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException("Could not restore the prior SSC sources.");
            }
        }

        ObserverView SmallBodyView(ObjectId id)
        {
            if (!snapshot.TryState(id, out var state) || NaturalBodyCatalogue.Find(id) is not { } body)
                throw new InvalidOperationException($"The original state for {id} is required.");
            return View(id, state, body.RadiusKilometers * (1 + focal / .85));
        }

        ObserverView View(ObjectId id, CartesianState state, double range)
        {
            Vector3d towardSun = (solar.Sun - state).PositionKilometers;
            Vector3d direction = towardSun + Vector3d.Cross(towardSun, new(0, 0, 1)) * .4;
            direction /= direction.Length;
            Vector3d up = new Vector3d(0, 0, 1) - direction * direction.Z;
            return new(epoch, CameraMode.Solar, id, false)
            {
                FieldOfViewDegrees = 49,
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(direction * range, direction * -1, up / up.Length) }
            };
        }

        void Save(ObserverView left, ObserverView right)
        {
            ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the planet photometry scenario.");
        }
    }

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
            if (!scene.Renderer(0).PlanetGlobeTextured(moon.Id) && scene.Renderer(0).BodyPoint(moon.Id)?.Visible != true)
                throw new InvalidOperationException($"The Jupiter scene must contain a resolved disc or photometric point for {moon.Name}.");

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
        var cometScene = ScenarioFile.Load(Path.Combine(directory, "comet-67p.stscene"));
        var visible = cometScene.Views[0];
        string legacyLayers = Path.Combine(directory, "legacy-body-layers.json");
        File.WriteAllText(legacyLayers, """{"Site":{},"Orbits":false,"CometTails":false,"Places":true}""");
        var hidden = LegacyObserverProfiles.Read(legacyLayers, visible, cometScene.DeltaT).Observer;
        string layerScene = Path.Combine(directory, "comet-67p-layer.stscene");
        ScenarioFile.Save(layerScene, cometScene with { Views = [visible, hidden] });
        if (!scene.LoadScenario(layerScene) || !await scene.ReloadAddOns() ||
            scene.Renderer(0).RenderedComet(comet) is null || scene.Renderer(1).RenderedComet(comet) is not null ||
            scene.Renderer(0).ShowSurfaceSiteLabels || !scene.Renderer(1).ShowSurfaceSiteLabels ||
            !scene.Renderer(0).ShowPlanetOrbits || scene.Renderer(1).ShowPlanetOrbits)
            throw new InvalidOperationException("Archived orbit, comet-tail and surface-place layers must remain independent after reload.");
        GD.Print("Archived body layers GPU: independent 67P comet activity, orbit and place preferences after reload.");
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
