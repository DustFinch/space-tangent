// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSpiceSsc(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPICE_SSC_GPU") is not { Length: > 0 } input)
            return;
        var original = new LocalAddonSettingsFile().Load();
        string prior = Path.Combine(directory, "before-spice-ssc.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save observers before installing Cassini SSC.");
        string fixture = Directory.CreateDirectory(Path.Combine(directory, "SSC Cassini")).FullName;
        string bodies = Directory.CreateDirectory(Path.Combine(fixture, "body catalogue")).FullName;
        string kernels = Directory.CreateDirectory(Path.Combine(fixture, "pointing kernels")).FullName;
        const string spk = "040615AP_SK_04167_04186.bsp", ck = "04182_04183pa_soi_itl.bc";
        const string frame = "cas_v43.tf", clock = "cas00172.tsc", mesh = "cassini.3ds";
        foreach (string file in new[] { spk, mesh })
            File.Copy(Path.Combine(input, file), Path.Combine(bodies, file), overwrite: true);
        foreach (string file in new[] { ck, frame, clock })
            File.Copy(Path.Combine(input, file), Path.Combine(kernels, file), overwrite: true);
        string originalCatalogue = Path.Combine(input, "cassini.ssc");
        string source = File.ReadAllText(originalCatalogue)
            .Replace($"\"{spk}\" \"{frame}\" \"{clock}\" \"{ck}\"", $"\"{spk}\"", StringComparison.Ordinal)
            .Replace("Radius 0.006", "Radius 0.006 OrbitFrame { EclipticJ2000 {} }", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(bodies, "cassini.ssc"), source);
        File.WriteAllText(Path.Combine(kernels, "pack.json"), """
            {"formatVersion":19,"spiceLoads":[{"policy":"furnish",
                "files":["cas_v43.tf","cas00172.tsc","04182_04183pa_soi_itl.bc"]}]}
            """);
        var bodySource = new LocalAddonSource(Guid.NewGuid(), LocalAddonKind.BodyCatalog, bodies, false, true);
        var kernelSource = new LocalAddonSource(Guid.NewGuid(), LocalAddonKind.BodyCatalog, kernels, false, true);
        var configured = original.SetSource(bodySource, true).SetSource(kernelSource, true);
        try
        {
            if (!await scene.ReloadAddOns(configured))
                throw new InvalidOperationException("Cassini SSC could not load its separately installed SPICE pointing kernels.");
            var session = scene.Session!;
            var track = new ObjectId("ssc/sol/saturn/cassini-ssc");
            var reference = SscSpiceCatalog.Load(originalCatalogue, input).Single();
            var epochs = new[] { JulianInstant.FromTdbSeconds(141917558.4203589), JulianInstant.FromTdbSeconds(141918830.41202343) };
            var views = epochs.Select(View).ToArray();
            var deltaT = session.DeltaT;
            string path = Path.Combine(directory, "spice-ssc-cassini.stscene");
            ScenarioFile.Save(path, new(views, false, 0, 0, deltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("The Cassini SSC scenario did not load.");
            for (int index = 0; index < views.Length; index++)
            {
                if (scene.Renderer(index).ImportedBodyNode(track) is not { Visible: true, Mesh: ArrayMesh model } ||
                    model.GetSurfaceCount() < 2)
                    throw new InvalidOperationException("An independent view lost the original multi-surface 3DS model.");
            }

            var published = session.World;
            string pointed = Path.Combine(kernels, ck);
            File.Delete(pointed);
            if (await scene.ReloadAddOns() || !ReferenceEquals(session.World, published) || session[0] != views[0] || session[1] != views[1])
                throw new InvalidOperationException("A missing SPICE kernel must roll back with both views preserved.");
            File.Copy(Path.Combine(input, ck), pointed);
            if (!await scene.ReloadAddOns() || session[0] != views[0] || session[1] != views[1] || session.DeltaT != deltaT)
                throw new InvalidOperationException("The corrected SSC installation lost a view or Delta T.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture Cassini SSC.");
            GD.Print("SSC SPICE GPU: two epochs retained original Cassini 3DS, SPK states and CK pointing with kernels in a second installed pack. A missing CK rolled back.");

            ObserverView View(JulianInstant epoch)
            {
                var snapshot = session.World.Graph.Evaluate(epoch);
                if (!snapshot.TryState(track, out var state) || !snapshot.TryState(PlanetCatalogue.SaturnId, out var saturn) ||
                    !reference.TrySample(epoch, out var expected) || !reference.TryAttitude(epoch, out var pointing) ||
                    !snapshot.TryOrientation(track, out var actual) ||
                    (state.PositionKilometers - saturn.PositionKilometers - expected.PositionKilometers).Length > .002 ||
                    (state.VelocityKilometersPerSecond - saturn.VelocityKilometersPerSecond - expected.VelocityKilometersPerSecond).Length > 1e-7 ||
                    (actual.ToWorld.X - pointing.X).Length > 1e-12 ||
                    (actual.ToWorld.Y - pointing.Y).Length > 1e-12 ||
                    (actual.ToWorld.Z - pointing.Z).Length > 1e-12)
                    throw new InvalidOperationException("Cassini SSC world state or pointing differs from the original track.");
                var direction = new Vector3d(.3, -.4, .8);
                direction /= direction.Length;
                var up = new Vector3d(0, 0, 1) - direction * direction.Z;
                const double range = .04;
                return new(epoch, CameraMode.Solar, track, false)
                {
                    Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, track, true)
                        { Placement = new NavigationPlacement(direction * range, direction * -1, up / up.Length) }
                };
            }
        }
        finally
        {
            if (!await scene.ReloadAddOns(original) || !scene.LoadScenario(prior))
                throw new InvalidOperationException("Could not restore the original installation after the Cassini SSC probe.");
        }
    }
}
