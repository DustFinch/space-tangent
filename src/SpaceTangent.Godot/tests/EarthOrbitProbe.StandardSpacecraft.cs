// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static void CheckMirBounds(MeshInstance3D node, double rangeKilometers)
    {
        // Reference bounds from the archived model decoder and its SSC center offset.
        var referenceMin = new Vector3(-1f, -.43728074f, -.9256078f);
        var referenceMax = new Vector3(1f, .49503687f, .9256078f);
        const float radiusKilometers = .0165f;
        var physicalScale = node.Transform.Basis.X.Length() * (float)(rangeKilometers / 48);
        var sourceOrigin = node.Transform.AffineInverse() * Vector3.Zero;
        var bounds = node.GetAabb();
        var min = (bounds.Position - sourceOrigin) * (physicalScale / radiusKilometers);
        var max = (bounds.End - sourceOrigin) * (physicalScale / radiusKilometers);
        if (min.DistanceTo(referenceMin) > 1e-5f || max.DistanceTo(referenceMax) > 1e-5f)
            throw new InvalidOperationException($"Mir model bounds differ from the archived renderer: {min}, {max}.");
        GD.Print("Mir GPU: centered bounds match the archived model, 33 m along the longest dimension.");
    }

    private async Task ExerciseStandardSpacecraft(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_CONIC") is not { Length: > 0 } source)
            return;

        const string input = "SPACETANGENT_SSC_ADDON_ROOT";
        string? previous = System.Environment.GetEnvironmentVariable(input);
        string original = Path.Combine(directory, "before-standard-spacecraft.stscene");
        if (!scene.SaveScenario(original))
            throw new IOException("Could not save the observers before importing the spacecraft.");

        try
        {
            System.Environment.SetEnvironmentVariable(input, source);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The original spacecraft catalogue failed to load.");
            var session = scene.Session!;
            await CheckPair("earth-spacecraft",
                View("ssc/sol/earth/hubble", 2458849.5, .048),
                View("ssc/sol/earth/mir", 2451544.5, .095));
            await CheckPair("moon-and-mars-spacecraft",
                View("ssc/sol/earth/moon/apollo-11", 2440423.0, .042),
                View("ssc/sol/mars/mars-odyssey", 2452623.5, .061));
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_GALILEO_GPU") == "1")
                await CheckPair("galileo-and-probe",
                    View("ssc/sol/jupiter/galileo", 2450083.500708, .058),
                    View("ssc/sol/jupiter/galileo/galileo-probe", 2450059.417375, .006));

            ObserverView View(string path, double day, double range)
            {
                var id = new ObjectId(path);
                var time = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
                var snapshot = session.World.Graph.Evaluate(time);
                if (!snapshot.TryState(id, out var state) || !snapshot.TryOrientation(id, out _))
                    throw new InvalidOperationException($"Missing original trajectory or orientation for {id.Value}.");
                var outward = new Vector3d(.33, -.45, .83);
                outward /= outward.Length;
                var up = new Vector3d(0, 0, 1) - outward * outward.Z;
                var camera = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(outward * range, outward * -1, up / up.Length) };
                return new(time, CameraMode.Solar, id, false) { Navigation = camera };
            }

            async Task CheckPair(string name, ObserverView first, ObserverView second)
            {
                string path = Path.Combine(directory, name + ".stscene");
                var deltaT = session.DeltaT;
                ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
                    session[0] != first || session[1] != second || session.DeltaT != deltaT)
                    throw new InvalidOperationException($"Observers, epochs or Delta T changed during reload of {name}.");

                for (int view = 0; view < 2; view++)
                {
                    var id = (view == 0 ? first : second).Navigation!.Value.Target!.Value;
                    if (scene.Renderer(view).ImportedBodyNode(id) is not MeshInstance3D { Visible: true, Mesh: ArrayMesh mesh } node ||
                        mesh.GetSurfaceCount() == 0)
                        throw new InvalidOperationException($"The original model for {id.Value} is not visible.");
                    GD.Print($"Standard spacecraft GPU: {id.Value}, {mesh.GetSurfaceCount()} material surfaces.");
                    if (id.Value.Contains("/galileo", StringComparison.Ordinal))
                    {
                        int textured = Enumerable.Range(0, mesh.GetSurfaceCount()).Count(index =>
                            mesh.SurfaceGetMaterial(index) is StandardMaterial3D { AlbedoTexture: not null });
                        if (textured == 0)
                            throw new InvalidOperationException($"No original material texture for {id.Value}.");
                        GD.Print($"Galileo GPU: {id.Value}, {textured} textured surfaces.");
                    }
                    if (id.Value == "ssc/sol/earth/mir")
                        CheckMirBounds(node, second.Navigation!.Value.RangeKilometers);
                }

                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException($"Could not capture {name}.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, previous);
        }

        if (!await scene.ReloadAddOns() || !scene.LoadScenario(original))
            throw new InvalidOperationException("Could not restore the original add-on sources and observers.");
    }
}
