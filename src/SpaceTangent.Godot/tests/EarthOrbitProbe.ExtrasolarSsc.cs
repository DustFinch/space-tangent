// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseExtrasolarSsc(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_EXTRASOLAR_SSC") is not { Length: > 0 } root)
            return;

        const string variable = "SPACETANGENT_SSC_ADDON_ROOT";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        string prior = Path.Combine(directory, "before-extrasolar-ssc.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observers before the extrasolar import.");
        try
        {
            System.Environment.SetEnvironmentVariable(variable, root);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("The extrasolar system failed to import.");

            var session = scene.Session!;
            var ringed = new ObjectId("ssc/sirius/test-ringed");
            var moon = new ObjectId("ssc/sirius/test-giant/test-moon-b");
            var star = new ObjectId("hip/32349");
            var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb);
            var first = Observer(ringed, epoch, 430_000);
            var second = Observer(moon, epoch.AddSeconds(86_400), 6500);
            string scenario = Path.Combine(directory, "sirius-rings-and-moon.stscene");
            var deltaT = session.DeltaT;
            ScenarioFile.Save(scenario, new([first, second], false, 0, 0, deltaT));
            if (!scene.LoadScenario(scenario) || !await scene.ReloadAddOns() || session[0] != first ||
                session[1] != second || session.DeltaT != deltaT)
                throw new InvalidOperationException("The two extrasolar observers must survive reload.");
            if (scene.Renderer(0).ImportedRingNode(ringed) is not { Visible: true, Mesh: ImmediateMesh mesh } ring ||
                ring.MaterialOverride is not ShaderMaterial material ||
                material.GetShaderParameter("rings_map").AsGodotObject() is not Texture2D ||
                Math.Abs(mesh.GetAabb().Size.X * 30_000 - 140_000) > .1 ||
                scene.Renderer(1).ImportedBodyNode(moon) is not { Visible: true })
                throw new InvalidOperationException("The original ring texture or the imported moon was not rendered.");

            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(star, out var hostState) || !snapshot.TryState(ringed, out var ringedState))
                throw new InvalidOperationException("The stellar host and ringed planet need states.");
            Vector3d direction = (hostState - ringedState).PositionKilometers;
            var expected = ring.Transform.Basis.Orthonormalized().Inverse() *
                new Vector3((float)direction.X, (float)direction.Z, (float)-direction.Y).Normalized();
            if (expected.DistanceTo(material.GetShaderParameter("sun_in_body").AsVector3()) > 1e-5f)
                throw new InvalidOperationException("The ring illumination must follow Sirius.");
            if (scene.Renderer(0).ImportedBodyNode(ringed)?.MaterialOverride is not ShaderMaterial globe ||
                expected.DistanceTo(globe.GetShaderParameter("light_in_body").AsVector3()) > 1e-5f)
                throw new InvalidOperationException("The planet illumination must follow Sirius.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(scenario, ".png")) != Error.Ok)
                throw new IOException("Could not capture the extrasolar rings and moon.");
            GD.Print("Extrasolar SSC GPU: Sirius illumination, original ring map, independent moon and ring cameras through reload.");

            ObserverView Observer(ObjectId id, JulianInstant time, double range)
            {
                if (!session.World.Graph.Evaluate(time).TryState(id, out var state))
                    throw new InvalidOperationException($"Missing extrasolar state for {id.Value}.");
                var direction = new Vector3d(-.8, .2, .56);
                direction /= direction.Length;
                var up = new Vector3d(0, 0, 1) - direction * direction.Z;
                return new(time, CameraMode.Solar, id, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                        { Placement = new NavigationPlacement(direction * range, direction * -1, up / up.Length) }
                };
            }
        }
        finally { System.Environment.SetEnvironmentVariable(variable, previous); }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the original installation.");
    }
}
