// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSourceFactories(SolarSystemScene scene, string directory)
    {
        const string sourceVariable = "SPACETANGENT_SSC_ADDON_ROOT";
        const string allowVariable = "SPACETANGENT_ALLOW_ADDON_LUA";
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_SCRIPTED_SSC") is not { Length: > 0 } source)
            return;
        string? previousSource = System.Environment.GetEnvironmentVariable(sourceVariable);
        string? previousAllow = System.Environment.GetEnvironmentVariable(allowVariable);
        var session = scene.Session!;
        var prior = session.CaptureScenario(0);
        string scenario = Path.Combine(directory, "scripted-cassini.stscene");
        var id = new ObjectId("ssc/sol/saturn/cassini-soi");
        try
        {
            System.Environment.SetEnvironmentVariable(sourceVariable, source);
            System.Environment.SetEnvironmentVariable(allowVariable, null);
            if (!await scene.ReloadAddOns() || session.World.Catalogue.Find(id) is not null ||
                session.World.SscBodies.Count != 2)
                throw new InvalidOperationException("An unopened Lua permission must leave the two source fallbacks only.");
            System.Environment.SetEnvironmentVariable(allowVariable, "1");
            if (!await scene.ReloadAddOns() || session.World.Catalogue.Find(id) is null || session.World.SscBodies.Count != 3)
                throw new InvalidOperationException("The allowed Cassini factory did not install through the SSC entry point.");
            var before = JulianInstant.FromJulianDate(2453187.55, TimeScale.Tdb);
            var after = JulianInstant.FromJulianDate(2453187.60, TimeScale.Tdb);
            var first = MakeView(before);
            var second = MakeView(after);
            ScenarioFile.Save(scenario, new([first, second], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(scenario) || !await scene.ReloadAddOns() || session[0] != first || session[1] != second)
                throw new InvalidOperationException("Both independent scripted Cassini views must survive reload.");
            var left = scene.Renderer(0).ImportedBodyNode(id);
            var right = scene.Renderer(1).ImportedBodyNode(id);
            if (left is not { Visible: true, Mesh: ArrayMesh firstMesh } || firstMesh.GetSurfaceCount() == 0 ||
                right is not { Visible: true } || left.Basis.X.Normalized().IsEqualApprox(right.Basis.X.Normalized()))
                throw new InvalidOperationException("The real Cassini geometry must follow different scripted attitudes in both views.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, "scripted-cassini-source.png")) != Error.Ok)
                    throw new IOException("Could not save the scripted Cassini source comparison.");
            }
            GD.Print($"Scripted Cassini source: {firstMesh.GetSurfaceCount()} real surfaces in two independent attitudes; permission and fallback verified.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(sourceVariable, previousSource);
            System.Environment.SetEnvironmentVariable(allowVariable, previousAllow);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("Could not restore the original body catalogues after testing scripted Cassini.");
            ScenarioFile.Save(scenario, prior);
            if (!scene.LoadScenario(scenario))
                throw new InvalidOperationException("Could not restore the previous views after testing scripted Cassini.");
        }

        ObserverView MakeView(JulianInstant epoch)
        {
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(id, out var state))
                throw new InvalidOperationException("The source factory did not return a covered state.");
            return new(epoch, CameraMode.Solar, id, false)
            {
                Navigation = new(state.PositionKilometers, .027, .53, .36, id, true)
            };
        }
    }
}
