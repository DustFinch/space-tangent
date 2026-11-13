// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseCrossSourceSsc(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CROSS_SOURCE_SSC_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK") is not { Length: > 0 } baseRoot)
            return;
        var file = new LocalAddonSettingsFile();
        var original = file.Load();
        string prior = Path.Combine(directory, "before-cross-source-ssc.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save observers before installing the cross-source SSC fixture.");

        string patches = Directory.CreateDirectory(Path.Combine(directory, "SSC overrides")).FullName;
        string patch = Path.Combine(patches, "station.ssc");
        const string modify = "Modify \"International Space Station\" \"Sol/Earth\" { Radius 0.1 }";
        const string broken = "Modify \"International Space Station\" \"Sol/Earth\" { Mesh \"missing.cmod\" }";
        File.WriteAllText(patch, modify);
        var installed = new LocalAddonSource(Guid.NewGuid(), LocalAddonKind.BodyCatalog, baseRoot, false, true);
        var overlay = new LocalAddonSource(Guid.NewGuid(), LocalAddonKind.BodyCatalog, patches, false, true);
        var configured = original.SetSource(installed, true).SetSource(overlay, true);
        try
        {
            if (!await scene.ReloadAddOns(configured))
                throw new InvalidOperationException("The ISS override did not load with its original catalogue.");
            var session = scene.Session!;
            var iss = new ObjectId("ssc/sol/earth/iss");
            var asteroid = new ObjectId("ssc/sol/2008-tc3");
            var epoch = JulianInstant.FromJulianDate(2454746.5, TimeScale.Tdb);
            var a = View(iss, epoch, .45);
            var b = View(asteroid, epoch.AddSeconds(60), .04);
            var deltaT = session.DeltaT;
            string path = Path.Combine(directory, "cross-source-ssc.stscene");
            ScenarioFile.Save(path, new([a, b], false, 0, 0, deltaT));
            if (!scene.LoadScenario(path) || session.World.SscBodies.Single(body => body.Id == iss).Definition is not
                { RadiusKilometers: .1, MeshRoot: { } owner } || !Path.GetFullPath(owner).Equals(Path.GetFullPath(baseRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The edit lost the original ISS model or its canonical ID.");
            var station = scene.Renderer(0).ImportedBodyNode(iss);
            if (station is not { Visible: true, Mesh: ArrayMesh mesh } ||
                !Enumerable.Range(0, mesh.GetSurfaceCount()).Any(index =>
                    mesh.SurfaceGetMaterial(index) is StandardMaterial3D { AlbedoTexture: not null }))
                throw new InvalidOperationException("The overridden ISS did not retain its original textured mesh.");
            var unchanged = session.World;
            File.WriteAllText(patch, broken);
            if (await scene.ReloadAddOns() || !ReferenceEquals(session.World, unchanged) || session[0] != a || session[1] != b)
                throw new InvalidOperationException("A missing overlay mesh must roll back without changing either observer.");
            File.WriteAllText(patch, modify);
            if (!await scene.ReloadAddOns() || session[0] != a || session[1] != b || session.DeltaT != deltaT)
                throw new InvalidOperationException("The repaired SSC overlay lost one observer or Delta T.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the original ISS and asteroid.");
            GD.Print("Cross-source SSC GPU: patched the original ISS through its alias, retained its textured CMOD and the independent 2008 TC3 observer, and rolled back a missing mesh.");

            ObserverView View(ObjectId id, JulianInstant at, double range)
            {
                if (!session.World.Graph.Evaluate(at).TryState(id, out var state))
                    throw new InvalidOperationException($"Missing original trajectory for {id.Value}.");
                var direction = new Vector3d(.3, -.4, .8);
                direction /= direction.Length;
                var up = new Vector3d(0, 0, 1) - direction * direction.Z;
                return new(at, CameraMode.Solar, id, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                        { Placement = new NavigationPlacement(direction * range, direction * -1, up / up.Length) }
                };
            }
        }
        finally
        {
            if (!await scene.ReloadAddOns(original) || !scene.LoadScenario(prior))
                throw new InvalidOperationException("Could not restore the original SSC installation.");
        }
    }
}
