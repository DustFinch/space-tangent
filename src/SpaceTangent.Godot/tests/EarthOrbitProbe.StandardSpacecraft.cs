// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
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
                    if (scene.Renderer(view).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } ||
                        mesh.GetSurfaceCount() == 0)
                        throw new InvalidOperationException($"The original model for {id.Value} is not visible.");
                    GD.Print($"Standard spacecraft GPU: {id.Value}, {mesh.GetSurfaceCount()} material surfaces.");
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
