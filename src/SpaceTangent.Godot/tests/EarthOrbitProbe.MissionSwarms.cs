// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionSwarms(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_SWARM_GPU") is not ("1" or "only") ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_SWARM_INSTALL") is not { Length: > 0 } corpus)
            return;
        const string input = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? original = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-swarm.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observers before importing the swarms.");
        try
        {
            var session = scene.Session!;
            var epoch = JulianInstant.FromJulianDate(2455755.25, TimeScale.Tdb);
            var asteroids = new ObjectId("mission/original-astorb");
            var hildas = new ObjectId("mission/hilda-family");
            var debris = new ObjectId("mission/fengyun-1c-debris");
            var legacy = System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SWARM_PACK");
            foreach (string format in legacy is null ? new[] { "source", "packed" } : new[] { "archived-v5" })
            {
                System.Environment.SetEnvironmentVariable(input, legacy ?? Path.Combine(corpus, format + "-addons", "swarms"));
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException($"Could not install the {format} swarms.");
                var first = View(asteroids, epoch, 1_300_000_000);
                var second = View(debris, epoch.AddSeconds(2 * 86400), 46_000);
                string path = Path.Combine(directory, $"mission-swarms-{format}.stscene");
                ScenarioFile.Save(path, new([first, second], false, 0, 0, session.DeltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                    throw new InvalidOperationException("Swarm cameras did not survive reload.");
                Need(0, asteroids, 5000);
                Need(0, hildas, 3094);
                // Scenario restoration and reload can finish before the secondary viewport is laid out.
                for (int frame = 0; frame < 8 && scene.Renderer(1).ImportedBodyNode(debris)?.Visible != true; frame++)
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var right = Need(1, debris, 2740);
                await Capture(path, scene.Renderer(1));

                var paired = View(debris, epoch, 46_000);
                string pairedPath = Path.ChangeExtension(path, ".paired.stscene");
                ScenarioFile.Save(pairedPath, new([paired, second], false, 0, 0, session.DeltaT));
                if (!scene.LoadScenario(pairedPath))
                    throw new InvalidOperationException("Could not open the paired swarm observers.");
                var left = Need(0, debris, 2740);
                if (left.Mesh != right.Mesh || left.MaterialOverride == right.MaterialOverride ||
                    Clock(left) == Clock(right))
                    throw new InvalidOperationException("Swarm data must be shared, and clocks and materials must be per view.");
                GD.Print($"Mission swarm GPU ({format}): 5,000 ASTORB, 3,094 Hilda, 2,740 Fengyun points; shared mesh and separate epochs.");
            }

            if (legacy is not null)
            {
                System.Environment.SetEnvironmentVariable(input, original);
                if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
                    throw new InvalidOperationException("Could not restore the installation after the archived swarm test.");
                return;
            }

            var translated = new ObjectId("mission/nativeswarmtranslated");
            var natural = new ObjectId("mission/nativeswarmnatural");
            var science = session.World.Satellites.Single(track => track.Name == "SORCE").Id;
            var captured = new Dictionary<(int View, ObjectId Body), Transform3D>();
            foreach (string format in new[] { "source", "packed" })
            {
                System.Environment.SetEnvironmentVariable(input, Path.Combine(corpus, format + "-addons", "science"));
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException($"Could not install the {format} rotating swarms.");
                var first = View(translated, epoch, 72_000);
                var second = View(natural, epoch.AddSeconds(3300), 72_000);
                string path = Path.Combine(directory, $"mission-swarm-frames-{format}.stscene");
                var deltaT = session.DeltaT;
                ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second ||
                    session.DeltaT != deltaT)
                    throw new InvalidOperationException("The rotating swarm observers did not survive reload.");
                for (int index = 0; index < 2; index++)
                {
                    var snapshot = session.World.Graph.Evaluate(session[index].Epoch);
                    if (!snapshot.TryState(SolarSystemSample.EarthId, out var earth) ||
                        !snapshot.TryOrientation(science, out var satellite))
                        throw new InvalidOperationException("Missing Earth or SORCE frame.");
                    var follow = Need(index, translated, 2740);
                    var spin = Need(index, natural, 2740);
                    if (follow.Mesh != spin.Mesh || follow.MaterialOverride == spin.MaterialOverride)
                        throw new InvalidOperationException("The two Fengyun references must share geometry and retain their own materials.");
                    foreach (var (id, node, offset) in new[] { (translated, follow, 12000), (natural, spin, -12000) })
                    {
                        if (!snapshot.TryState(id, out var state) ||
                            ((state - earth).PositionKilometers - new Vector3d(offset, 0, 0)).Length > .001)
                            throw new InvalidOperationException("The swarm lost its authored offset from Earth.");
                        if (format == "source")
                            captured.Add((index, id), node.Transform);
                        else
                        {
                            var priorPose = captured[(index, id)];
                            if (node.Position.DistanceTo(priorPose.Origin) > .001 ||
                                DirectionError(node.Basis, priorPose.Basis) > 1e-5f)
                                throw new InvalidOperationException($"Source and packed swarm poses differ for {id.Value} in view {index + 1}: {DirectionError(node.Basis, priorPose.Basis)}.");
                        }
                    }
                    var alongSorCe = ToRender(satellite.ToWorld.Z);
                    if (follow.Basis.Z.Normalized().Dot(alongSorCe) < .55f)
                        throw new InvalidOperationException("The translated swarm is detached from the live SORCE frame.");
                }
                if (DirectionError(Need(0, natural, 2740).Basis, Need(1, natural, 2740).Basis) < .25)
                    throw new InvalidOperationException("The rotating swarm did not advance independently.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not capture the rotating swarm observers.");
                GD.Print($"Mission swarm frames ({format}): SORCE following, two 12,000 km offsets, two independent spin epochs and shared buffers after reload.");
            }

            static Vector3 ToRender(Vector3d axis) => new((float)axis.X, (float)axis.Z, (float)-axis.Y);

            static float DirectionError(Basis first, Basis second) => Math.Max(
                first.X.Normalized().DistanceTo(second.X.Normalized()),
                Math.Max(first.Y.Normalized().DistanceTo(second.Y.Normalized()),
                    first.Z.Normalized().DistanceTo(second.Z.Normalized())));

            ObserverView View(ObjectId id, JulianInstant at, double range)
            {
                if (!session.World.Graph.Evaluate(at).TryState(id, out var state))
                    throw new InvalidOperationException($"Missing swarm {id.Value}.");
                var eye = new Vector3d(.32, -.45, .83);
                eye /= eye.Length;
                var up = new Vector3d(0, 0, 1) - eye * eye.Z;
                return new(at, CameraMode.Solar, id, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                        { Placement = new NavigationPlacement(eye * range, eye * -1, up / up.Length) }
                };
            }

            MeshInstance3D Need(int view, ObjectId id, int count)
            {
                var node = scene.Renderer(view).ImportedBodyNode(id)
                    ?? throw new InvalidOperationException($"Missing installed swarm {id.Value}.");
                if (!node.Visible || node.Mesh is not ArrayMesh mesh ||
                    mesh.SurfaceGetPrimitiveType(0) != Mesh.PrimitiveType.Points || mesh.SurfaceGetArrayLen(0) != count)
                    throw new InvalidOperationException($"Wrong point count or visibility for {id.Value}.");
                return node;
            }

            static float Clock(MeshInstance3D node) =>
                ((ShaderMaterial)node.MaterialOverride).GetShaderParameter("swarm_clock").AsSingle();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, original);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the installation after the swarm tests.");
    }

    private async Task Capture(string path, SimulationViewport renderer)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var full = GetViewport().GetTexture().GetImage();
        if (full.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the swarm observers.");
        using var view = renderer.GetViewport().GetTexture().GetImage();
        int colored = 0;
        for (int y = 0; y < view.GetHeight(); y++)
        for (int x = 0; x < view.GetWidth(); x++)
        {
            Color pixel = view.GetPixel(x, y);
            if (pixel.R > .4f && pixel.R > 1.5f * pixel.G && Math.Abs(pixel.G - pixel.B) < .08f)
                colored++;
        }
        if (colored < 200)
            throw new InvalidOperationException($"Expected rendered Fengyun particles, but found {colored} red samples.");
    }
}
