// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMinorMoons(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MINOR_MOONS_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_MINOR_MOONS") is not { Length: > 0 } root)
            return;
        const string input = "SPACETANGENT_SSC_ADDON_ROOT";
        string? original = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-minor-moons.stscene");
        if (!scene.SaveScenario(prior))
            throw new InvalidOperationException("Could not save the existing scene.");
        try
        {
            System.Environment.SetEnvironmentVariable(input, root);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The original minor moon catalogue did not load.");
            var session = scene.Session!;
            var metis = new ObjectId("ssc/sol/jupiter/metis");
            var himalia = new ObjectId("ssc/sol/jupiter/himalia");
            if (session.World.SscBodies.Count != 183 ||
                !session.World.Catalogue.Search("Jupiter XVI").Any(entry => entry.Id == metis && entry.Kind == CatalogueKind.Moon))
                throw new InvalidOperationException("Expected 183 installed minor moons and the original Metis alias.");
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            var first = View(metis, epoch, 100);
            var second = View(himalia, epoch.AddSeconds(7200), 360);
            string path = Path.Combine(directory, "original-minor-moons.stscene");
            ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                throw new InvalidOperationException("The minor moon cameras must survive reload.");
            Check(0, metis, 100);
            Check(1, himalia, 9000);
            var himaliaNode = scene.Renderer(1).ImportedBodyNode(himalia)!;
            Aabb raw = himaliaNode.Mesh.GetAabb();
            var basis = himaliaNode.Transform.Basis;
            Vector3 diameters = new(raw.Size.X * basis.X.Length(), raw.Size.Y * basis.Y.Length(), raw.Size.Z * basis.Z.Length());
            if (diameters.DistanceTo(new Vector3(205.6f, 141.4f, 205.6f) * (48f / 360)) > .001f)
                throw new InvalidOperationException($"The published Himalia mesh lost its physical semiaxes: {diameters}.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var picture = GetViewport().GetTexture().GetImage();
            if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture both minor moons.");
            GD.Print("Minor moon GPU: 183 real catalogue entries, Metis alias, original Metis and Himalia models, independent epochs and reload.");

            ObserverView View(ObjectId id, JulianInstant time, double range)
            {
                var snapshot = session.World.Graph.Evaluate(time);
                if (!snapshot.TryState(id, out var state) || !snapshot.TryOrientation(id, out _) || snapshot.SolarSystem is not { } solar)
                    throw new InvalidOperationException($"Missing state or orientation for {id}.");
                var sun = (solar.Sun - state).PositionKilometers;
                var outward = Unit(sun + Vector3d.Cross(sun, new(0, 0, 1)) * .6);
                var north = Unit(new Vector3d(0, 0, 1) - outward * outward.Z);
                var camera = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(outward * range, outward * -1, north) };
                return new(time, CameraMode.Solar, id, false) { Navigation = camera };
            }

            void Check(int view, ObjectId id, int minimumTriangles)
            {
                if (scene.Renderer(view).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } ||
                    Enumerable.Range(0, mesh.GetSurfaceCount()).Sum(mesh.SurfaceGetArrayLen) < 3 * minimumTriangles ||
                    mesh.SurfaceGetMaterial(0) is not ShaderMaterial material ||
                    !material.GetShaderParameter("has_diffuse").AsBool())
                    throw new InvalidOperationException($"No published original mesh for {id}.");
            }
        }
        finally { System.Environment.SetEnvironmentVariable(input, original); }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the previous add-on installation.");

        static Vector3d Unit(Vector3d vector) => vector / vector.Length;
    }
}
