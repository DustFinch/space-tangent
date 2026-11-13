// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSmallBodyRings(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SMALL_RINGS") is not { Length: > 0 } source)
            return;

        const string input = "SPACETANGENT_SSC_ADDON_ROOT";
        string? previous = System.Environment.GetEnvironmentVariable(input);
        string original = Path.Combine(directory, "before-small-rings.stscene");
        if (!scene.SaveScenario(original))
            throw new IOException("Could not save the observers before importing the rings.");
        try
        {
            System.Environment.SetEnvironmentVariable(input, source);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The original rings failed to load.");
            var session = scene.Session!;
            await CheckPair("centaur-rings", ("2060-chiron", 341.66, 353.77), ("10199-chariklo", 382.5, 399.9));
            await CheckPair("dwarf-planet-rings", ("haumea", 2252, 2322), ("quaoar", 2515, 4098));

            async Task CheckPair(string name, (string Slug, double Inner, double Outer) a, (string Slug, double Inner, double Outer) b)
            {
                var first = Observer(a.Slug, 2457774.6321, a.Outer);
                var second = Observer(b.Slug, 2459800.7744, b.Outer);
                string path = Path.Combine(directory, name + ".stscene");
                var deltaT = session.DeltaT;
                ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first ||
                    session[1] != second || session.DeltaT != deltaT)
                    throw new InvalidOperationException("The original ring cameras did not survive reload.");
                Check(0, first, a.Inner, a.Outer);
                Check(1, second, b.Inner, b.Outer);

                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not capture the original rings.");
            }

            ObserverView Observer(string slug, double day, double outer)
            {
                var id = new ObjectId("ssc/sol/" + slug);
                var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
                var snapshot = session.World.Graph.Evaluate(epoch);
                if (!snapshot.TryState(id, out var body) || !snapshot.TryOrientation(id, out var frame))
                    throw new InvalidOperationException($"No barycentric state or frame for {slug}.");
                var eye = frame.ToWorld.Z + frame.ToWorld.X * .5;
                eye /= eye.Length;
                var up = frame.ToWorld.Y;
                double range = outer * 3;
                return new(epoch, CameraMode.Solar, id, false)
                {
                    Navigation = new NavigationCamera(body.PositionKilometers, range, 0, 0, id, true)
                        { Placement = new NavigationPlacement(eye * range, eye * -1, up) }
                };
            }

            void Check(int view, ObserverView observer, double inner, double outer)
            {
                ObjectId id = observer.Selection!.Value;
                if (scene.Renderer(view).ImportedRingNode(id) is not { Visible: true, Mesh: ImmediateMesh mesh } ring ||
                    ring.MaterialOverride is not ShaderMaterial material ||
                    material.GetShaderParameter("rings_map").AsGodotObject() is not Texture2D)
                    throw new InvalidOperationException($"No visible textured ring for {id.Value}.");
                var body = session.World.SscBodies.Single(item => item.Id == id);
                var bounds = mesh.GetAabb();
                if (body.Definition.Rings?.InnerKilometers != inner || body.Definition.Rings?.OuterKilometers != outer ||
                    Math.Abs(bounds.Size.X * body.Definition.RadiusKilometers / 2 - outer) > .002)
                    throw new InvalidOperationException($"The authored radii changed for {id.Value}.");
                if (!session.World.Graph.Evaluate(observer.Epoch).TryOrientation(id, out var orientation))
                    throw new InvalidOperationException("The ring requires a body frame.");
                var pole = orientation.ToWorld.Z;
                if (ring.Transform.Basis.Y.Normalized().DistanceTo(new((float)pole.X, (float)pole.Z, (float)-pole.Y)) > 1e-5f)
                    throw new InvalidOperationException("The ring plane did not follow the body's equator.");
                GD.Print($"Small-body rings GPU: {id.Value}, original map and {inner}–{outer} km radii.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, previous);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(original))
            throw new InvalidOperationException("The original installation could not be restored after the ring test.");
    }
}
