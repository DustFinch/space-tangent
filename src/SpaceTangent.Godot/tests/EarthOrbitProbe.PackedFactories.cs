// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExercisePackedFactories(SolarSystemScene scene, string directory)
    {
        const string sourceVariable = "SPACETANGENT_SSC_ADDON_ROOT";
        const string allowVariable = "SPACETANGENT_ALLOW_ADDON_LUA";
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_SCRIPTED_PACK") is not { Length: > 0 } packed ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_SCRIPTED_SSC") is not { Length: > 0 } source)
            return;
        string? previousSource = System.Environment.GetEnvironmentVariable(sourceVariable);
        string? previousAllow = System.Environment.GetEnvironmentVariable(allowVariable);
        var session = scene.Session!;
        var prior = session.CaptureScenario(0);
        string path = Path.Combine(directory, "scripted-cassini-packed.stscene");
        var id = new ObjectId("ssc/sol/saturn/cassini-soi");
        try
        {
            System.Environment.SetEnvironmentVariable(allowVariable, "1");
            System.Environment.SetEnvironmentVariable(sourceVariable, source);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("Could not load the comparison SSC source.");
            var first = JulianInstant.FromJulianDate(2453187.55, TimeScale.Tdb);
            var second = JulianInstant.FromJulianDate(2453187.60, TimeScale.Tdb);
            var sourceFirst = Sample(first);
            var sourceSecond = Sample(second);
            var scenario = new SimulationScenario([Look(first, sourceFirst.State), Look(second, sourceSecond.State)],
                false, 0, 0, session.DeltaT);
            ScenarioFile.Save(path, scenario);
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the source observers for the packed comparison.");
            System.Environment.SetEnvironmentVariable(sourceVariable, packed);
            if (!await scene.ReloadAddOns() || Sample(first) != sourceFirst || Sample(second) != sourceSecond ||
                session[0] != scenario.Views[0] || session[1] != scenario.Views[1])
                throw new InvalidOperationException("Switching to the original pack changed a trajectory, spin or observer.");
            var left = scene.Renderer(0).ImportedBodyNode(id);
            var right = scene.Renderer(1).ImportedBodyNode(id);
            if (left is not { Visible: true, Mesh: ArrayMesh mesh } || mesh.GetSurfaceCount() == 0 ||
                right is not { Visible: true } || left.Basis.X.Normalized().IsEqualApprox(right.Basis.X.Normalized()))
                throw new InvalidOperationException("The packed GLB must render its two distinct attitudes.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, "scripted-cassini-packed.png")) != Error.Ok)
                    throw new IOException("Could not save the scripted packed Cassini comparison.");
            }
            GD.Print($"Scripted Cassini pack: source states and attitudes identical in both views, {mesh.GetSurfaceCount()} packed surfaces.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(sourceVariable, previousSource);
            System.Environment.SetEnvironmentVariable(allowVariable, previousAllow);
            if (!await scene.ReloadAddOns())
                throw new InvalidOperationException("Could not restore the original body catalogues.");
            ScenarioFile.Save(path, prior);
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not restore the previous views.");
        }

        (CartesianState State, Basis3d Attitude) Sample(JulianInstant epoch)
        {
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(id, out var state) || !snapshot.TryOrientation(id, out var attitude))
                throw new InvalidOperationException("The scripted Cassini state or spin was unavailable.");
            return (state, attitude.ToWorld);
        }

        ObserverView Look(JulianInstant epoch, CartesianState state) => new(epoch, CameraMode.Solar, id, false)
        {
            Navigation = new(state.PositionKilometers, .027, .53, .36, id, true)
        };
    }
}
