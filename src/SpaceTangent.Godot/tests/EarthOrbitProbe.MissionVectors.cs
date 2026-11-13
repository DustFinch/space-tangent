// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionVectors(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_VECTORS_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_VECTORS") is not { Length: > 0 } corpus)
            return;
        const string input = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? installed = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-mission-vectors.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observers before installing the mission visualizers.");
        try
        {
            var session = scene.Session!;
            var ecliptic = new ObjectId("mission/ecliptic-plane");
            var equatorial = new ObjectId("mission/equatorial-plane");
            var direction = new ObjectId("mission/tomoon");
            var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
            var first = View(epoch);
            var second = View(epoch.AddSeconds(5 * 86400));
            var deltaT = session.DeltaT;

            foreach (string format in new[] { "source", "packed" })
            {
                System.Environment.SetEnvironmentVariable(input, Path.Combine(corpus, "p4-vector-" + format));
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException($"Could not install the {format} visualizers.");
                string path = Path.Combine(directory, $"mission-vectors-{format}.stscene");
                ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path))
                    throw new InvalidOperationException($"Could not open the {format} visualizer scenario.");
                Verify();
                if (!await scene.ReloadAddOns() || session[0] != first || session[1] != second || session.DeltaT != deltaT)
                    throw new InvalidOperationException("The two visualizer cameras and Delta T must survive reload.");
                Verify();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not capture the mission visualizers.");
                GD.Print($"Mission vectors GPU ({format}): 20-by-20 ecliptic and equatorial grids, two lunar directions at independent epochs, and both cameras restored.");
            }

            ObserverView View(JulianInstant when)
            {
                var target = SolarSystemSample.EarthId;
                var state = session.World.Ephemeris.Sample(when).Earth;
                var eye = new Vector3d(.23, -.61, .76);
                eye /= eye.Length;
                var up = new Vector3d(0, 0, 1) - eye * eye.Z;
                const double range = 77_000;
                return new(when, CameraMode.Solar, target, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, target, true)
                        { Placement = new NavigationPlacement(eye * range, eye * -1, up / up.Length) }
                };
            }

            void Verify()
            {
                for (int index = 0; index < 2; index++)
                {
                    var renderer = scene.Renderer(index);
                    var a = Need(renderer, ecliptic, 2);
                    var b = Need(renderer, equatorial, 2);
                    var arrow = Need(renderer, direction, 1);
                    var labels = arrow.GetChildren().OfType<Label3D>();
                    if (labels.SingleOrDefault() is not { Text: "→ Moon", Visible: true, NoDepthTest: false } label ||
                        label.GlobalPosition.DistanceTo(arrow.GlobalPosition + arrow.Basis.X) > .6f)
                        throw new InvalidOperationException("The lunar direction label did not follow the arrow tip.");
                    float tilt = a.Basis.Y.AngleTo(b.Basis.Y) * 180 / Mathf.Pi;
                    if (Math.Abs(tilt - 23.4393f) > .001f)
                        throw new InvalidOperationException($"The ecliptic grid tilt was {tilt} degrees.");
                    foreach (var plane in new[] { a, b })
                    {
                        using var grid = ((ArrayMesh)plane.Mesh).SurfaceGetArrays(1);
                        if (grid[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length != 84 ||
                            ((ArrayMesh)plane.Mesh).SurfaceGetMaterial(0) is not StandardMaterial3D material ||
                            Math.Abs(material.AlbedoColor.A - .2f) > .001)
                            throw new InvalidOperationException("A plane lost its authored grid or opacity.");
                    }
                    var solar = session.World.Ephemeris.Sample(session[index].Epoch);
                    Vector3d toMoon = (solar.Moon - solar.Earth).PositionKilometers;
                    Vector3 expected = new((float)toMoon.X, (float)toMoon.Z, (float)-toMoon.Y);
                    if (expected.Normalized().DistanceTo(arrow.Basis.X.Normalized()) > 1e-5f ||
                        Math.Abs(arrow.Basis.X.Length() - 20_000 * 48f / 77_000) > .001f)
                        throw new InvalidOperationException("The arrow did not point to the Moon at this view's epoch.");
                }
                var left = Need(scene.Renderer(0), direction, 1);
                var right = Need(scene.Renderer(1), direction, 1);
                if (left.Basis.X.AngleTo(right.Basis.X) < .2f)
                    throw new InvalidOperationException("The lunar direction did not change across the independent epochs.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, installed);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the installation after checking the visualizers.");

        static MeshInstance3D Need(SimulationViewport renderer, ObjectId id, int surfaces) =>
            renderer.ImportedBodyNode(id) is { Visible: true, Mesh: ArrayMesh mesh } node && mesh.GetSurfaceCount() == surfaces
                ? node : throw new InvalidOperationException($"Missing mission visualizer {id.Value}.");
    }
}
