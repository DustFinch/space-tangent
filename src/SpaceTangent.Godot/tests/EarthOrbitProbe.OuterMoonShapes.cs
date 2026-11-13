// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMoonPhotometry(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (DisplayServer.GetName() == "headless" ||
            string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("SPACETANGENT_OUTER_SHAPE_ORACLE")))
            return;
        var id = new ObjectId("sol/hyperion");
        var body = NaturalBodyCatalogue.Find(id)!;
        var session = scene.Session!;
        var snapshot = session.World.Graph.Evaluate(epoch);
        if (!snapshot.TryState(id, out var state))
            throw new InvalidOperationException("The original Hyperion orbit is required.");
        Vector3d sun = (snapshot.SolarSystem!.Value.Sun - state).PositionKilometers;
        Vector3d eye = sun + Vector3d.Cross(sun, new(0, 0, 1)) * .4;
        eye /= eye.Length;
        Vector3d up = new Vector3d(0, 0, 1) - eye * eye.Z;
        up /= up.Length;
        string path = Path.Combine(directory, "hyperion-point-and-mesh.stscene");
        ObserverView near = At(body.RadiusKilometers * 4.2);
        Save([near, near]);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var renderer = scene.Renderer(0);
        double focal = renderer.GetViewport().GetVisibleRect().Size.Y / (2 * Math.Tan(49 * Math.PI / 360));
        ObserverView far = At(body.RadiusKilometers * (1 + focal / .55));
        ObserverView blend = At(body.RadiusKilometers * (1 + focal / 2.5));
        Save([blend, near]);
        if (renderer.MoonPoint(id) is not { Visible: true, MaterialOverride: ShaderMaterial blendMaterial } ||
            renderer.NaturalMoonModel(id) is not { Visible: true } ||
            Math.Abs(blendMaterial.GetShaderParameter("fade").AsSingle() - .5f) > .01f)
            throw new InvalidOperationException("Hyperion must blend its original mesh and magnitude point between one and four pixels.");

        Save([far, near]);
        if (renderer.MoonPoint(id) is not { Visible: true } point ||
            renderer.NaturalMoonModel(id) is not { Visible: false } ||
            scene.Renderer(1).NaturalMoonModel(id) is not { Visible: true } ||
            scene.Renderer(1).MoonPoint(id)?.Visible == true)
            throw new InvalidOperationException("Only the distant view should replace the original Hyperion mesh with its point.");
        if (renderer.PickObject(renderer.GetViewport().GetVisibleRect().Size / 2) != id)
            throw new InvalidOperationException("The unresolved Hyperion point must remain selectable.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var shown = renderer.GetViewport().GetTexture().GetImage();
        Vector2I center = shown.GetSize() / 2;
        point.Hide();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var clear = renderer.GetViewport().GetTexture().GetImage();
        point.Show();
        if (shown.GetPixel(center.X, center.Y).IsEqualApprox(clear.GetPixel(center.X, center.Y)))
            throw new InvalidOperationException("The unresolved Hyperion point must change the center pixel.");
        var expected = session.CaptureScenario(0);
        if (!await scene.ReloadAddOns())
            throw new InvalidOperationException("Could not reload the original Hyperion data.");
        for (int index = 0; index < expected.Views.Count; index++)
        {
            var restored = scene.Session![index];
            var original = expected.Views[index];
            if (restored.Selection != id || restored.Epoch != original.Epoch ||
                restored.Navigation is not { Tracking: true, Placement: { } actual } ||
                original.Navigation is not { Placement: { } prior } ||
                (actual.OffsetKilometers - prior.OffsetKilometers).Length > .001 ||
                (actual.Forward - prior.Forward).Length > 1e-9 || (actual.Up - prior.Up).Length > 1e-9)
                throw new InvalidOperationException($"The Hyperion camera in view {index + 1} changed after reload.");
        }
        if (scene.Renderer(0).MoonPoint(id)?.Visible != true ||
            scene.Renderer(1).NaturalMoonModel(id)?.Visible != true)
            throw new InvalidOperationException("Reloading must preserve the point and the original mesh in their respective views.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var comparison = GetViewport().GetTexture().GetImage();
        if (comparison.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the Hyperion point and mesh.");
        GD.Print("Moon photometry GPU: original Hyperion mesh, 2.5-pixel blend, unresolved point, picking and reload.");

        const double orbitRadius = 1500934 * 1.0274;
        var hidden = WithLabel(At(focal * orbitRadius / 15));
        var faded = WithLabel(At(focal * orbitRadius / 30));
        Save([faded, hidden]);
        var label = scene.Renderer(0).MoonLabel(id)!;
        if (!label.Visible || Math.Abs(label.Modulate.A - .5f) > .01f ||
            scene.Renderer(1).MoonLabel(id)?.Visible != false)
            throw new InvalidOperationException("Hyperion labels must fade between 20 and 40 projected orbit pixels.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var labeled = renderer.GetViewport().GetTexture().GetImage();
        label.Hide();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var unlabeled = renderer.GetViewport().GetTexture().GetImage();
        var screen = renderer.NavigationCameraNode.UnprojectPosition(label.Position);
        int changed = 0;
        for (int y = Math.Max(0, (int)screen.Y - 16); y < Math.Min(labeled.GetHeight(), (int)screen.Y + 16); y++)
        for (int x = Math.Max(0, (int)screen.X - 64); x < Math.Min(labeled.GetWidth(), (int)screen.X + 64); x++)
            if (!labeled.GetPixel(x, y).IsEqualApprox(unlabeled.GetPixel(x, y)))
                changed++;
        if (changed < 20)
            throw new InvalidOperationException($"The Hyperion label changed only {changed} pixels.");
        label.Show();

        if (!snapshot.TryState(body.Parent, out var parent))
            throw new InvalidOperationException("The original Saturn state is required.");
        Vector3d towardParent = parent.PositionKilometers - state.PositionKilometers;
        Vector3d direction = towardParent / towardParent.Length;
        Vector3d occultedUp = new Vector3d(0, 0, 1) - direction * direction.Z;
        var occulted = WithLabel(At(2 * towardParent.Length));
        occulted = occulted with
        {
            Navigation = occulted.Navigation!.Value with
            {
                Placement = new NavigationPlacement(towardParent * 2, direction * -1, occultedUp / occultedUp.Length)
            }
        };
        Save([occulted, faded with { Sky = default }]);
        if (scene.Renderer(0).MoonLabel(id)?.Visible != false ||
            scene.Renderer(1).MoonLabel(id)?.Visible != false)
            throw new InvalidOperationException("Saturn occultation and the independent Labels layer must each hide Hyperion.");
        GD.Print($"Moon labels GPU: original Hyperion fade, {changed} label pixels, Saturn occultation and independent visibility.");

        static ObserverView WithLabel(ObserverView observer) => observer with { Sky = new(false, true, false, false) };

        void Save(ObserverView[] views)
        {
            ScenarioFile.Save(path, new(views, false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the Hyperion photometry scenario.");
        }

        ObserverView At(double distance) => new(epoch, CameraMode.Solar, id, false)
        {
            FieldOfViewDegrees = 49,
            Navigation = new NavigationCamera(state.PositionKilometers, distance, 0, 0, id, true)
                { Placement = new NavigationPlacement(eye * distance, eye * -1, up) }
        };
    }

    private async Task ExerciseOuterMoonShapes(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_OUTER_SHAPE_ORACLE") is not { } reference)
            return;
        var session = scene.Session!;
        using var oracle = JsonDocument.Parse(File.ReadAllText(reference));
        var source = oracle.RootElement.EnumerateArray().ToArray();
        for (int pair = 0; pair < source.Length; pair += 2)
        {
            var a = source[pair];
            var b = source[(pair + 1) % source.Length];
            var left = Observer(a, epoch);
            var right = Observer(b, epoch.AddSeconds(3600));
            string name = "outer-moon-shapes-" + pair;
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the normalized outer moon shapes.");
            if (pair == 0 && (!await scene.ReloadAddOns() || session[0] != left || session[1] != right))
                throw new InvalidOperationException("The original moon models or cameras failed to reload.");
            Check(0, left, a);
            Check(1, right, b);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the original outer moon shapes.");
        }

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_HYPERION_SPIN") is { } spinReference)
        {
            using var spin = JsonDocument.Parse(File.ReadAllText(spinReference));
            var hyperion = source.Single(entry => entry.GetProperty("name").GetString() == "hyperion");
            var first = spin.RootElement[2];
            var second = spin.RootElement[3];
            var left = Observer(hyperion, Instant(first));
            var right = Observer(hyperion, Instant(second));
            string path = Path.Combine(directory, "hyperion-spin.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != left || session[1] != right)
                throw new InvalidOperationException("The two Hyperion epochs or cameras changed after reloading.");
            CheckSpin(0, first);
            CheckSpin(1, second);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the two Hyperion orientations.");
            GD.Print("Hyperion GPU: original mesh axes match the archived precession in both views after reload.");

            static JulianInstant Instant(JsonElement row) => JulianInstant.FromJulianDate(row[0].GetDouble(), TimeScale.Tdb);

            void CheckSpin(int view, JsonElement row)
            {
                if (scene.Renderer(view).NaturalMoonModel(new("sol/hyperion")) is not { Visible: true } model)
                    throw new InvalidOperationException("The original Hyperion mesh is missing.");
                var basis = model.Transform.Basis;
                if (basis.X.Normalized().DistanceTo(Axis(1)) > 1e-5f ||
                    basis.Y.Normalized().DistanceTo(Axis(4)) > 1e-5f ||
                    basis.Z.Normalized().DistanceTo(Axis(7)) > 1e-5f)
                    throw new InvalidOperationException("The rendered Hyperion axes differ from the archive.");
                Vector3 Axis(int index) => new(row[index].GetSingle(), row[index + 2].GetSingle(), -row[index + 1].GetSingle());
            }
        }

        ObserverView Observer(JsonElement entry, JulianInstant instant)
        {
            string name = entry.GetProperty("name").GetString()!;
            var id = new ObjectId("sol/" + (name == "roughsphere" ? "nereid" : name));
            var body = NaturalBodyCatalogue.Find(id)!;
            var snapshot = session.World.Graph.Evaluate(instant);
            if (!snapshot.TryState(id, out var state))
                throw new InvalidOperationException($"Missing {body.Name} state at the model probe epoch.");
            Vector3d sun = (snapshot.SolarSystem!.Value.Sun - state).PositionKilometers;
            Vector3d eye = sun + Vector3d.Cross(sun, new(0, 0, 1)) * .4;
            eye /= eye.Length;
            Vector3d up = new Vector3d(0, 0, 1) - eye * eye.Z;
            up /= up.Length;
            double range = body.RadiusKilometers * 4.2;
            return new(instant, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(eye * range, eye * -1, up) }
            };
        }

        void Check(int view, ObserverView observer, JsonElement expected)
        {
            ObjectId id = observer.Selection!.Value;
            if (scene.Renderer(view).NaturalMoonModel(id) is not { Visible: true, Mesh: ArrayMesh mesh } ||
                mesh.GetSurfaceCount() != 1 ||
                mesh.SurfaceGetArrayLen(0) != expected.GetProperty("TriangleCount").GetInt32() * 3 ||
                mesh.SurfaceGetMaterial(0) is not ShaderMaterial material)
                throw new InvalidOperationException($"{id.Value} did not render the original triangle mesh. " +
                    $"Viewport {scene.Renderer(view).GetViewport().GetVisibleRect().Size}, " +
                    $"point {scene.Renderer(view).MoonPoint(id)?.Visible}, model {scene.Renderer(view).NaturalMoonModel(id)?.Visible}.");
            Aabb bounds = mesh.GetAabb();
            if (bounds.Position.DistanceTo(Vector(expected.GetProperty("minimum"))) > .001 ||
                bounds.End.DistanceTo(Vector(expected.GetProperty("maximum"))) > .001)
                throw new InvalidOperationException($"{id.Value} differs from the normalized reference bounds: {bounds}.");
            bool textured = id.Value != "sol/nereid";
            if (material.GetShaderParameter("has_diffuse").AsBool() != textured)
                throw new InvalidOperationException($"{id.Value} did not bind its original surface map.");
            using var arrays = mesh.SurfaceGetArrays(0);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            double area = 0;
            for (int i = 0; i < vertices.Length; i += 3)
                area += (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]).Length() * .5;
            if (Math.Abs(area / expected.GetProperty("area").GetDouble() - 1) > .00001)
                throw new InvalidOperationException($"{id.Value} differs from the reference mesh surface area: {area}.");
            GD.Print($"Outer moon {id.Value}: {vertices.Length / 3} reference triangles, physical bounds and area; texture={textured}.");
        }

        static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    }
}
