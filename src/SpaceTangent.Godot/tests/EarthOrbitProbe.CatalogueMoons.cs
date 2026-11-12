// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseCatalogueMoons(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CATALOGUE_MOONS_GPU") != "1")
            return;
        var session = scene.Session!;
        var phoebe = new ObjectId("sol/phoebe");
        var proteus = new ObjectId("sol/proteus");
        var nereid = new ObjectId("sol/nereid");
        var phobos = new ObjectId("sol/phobos");
        var deimos = new ObjectId("sol/deimos");
        var historic = JulianInstant.FromJulianDate(2159358.5, TimeScale.Tdb);
        var next = epoch.AddSeconds(3600);
        foreach (JulianInstant at in new[] { epoch, historic })
        {
            var world = session.World;
            var snapshot = world.Graph.Evaluate(at);
            foreach (var (id, minimum, maximum) in new[]
                {
                    (phobos, 9000d, 9600d), (deimos, 23000d, 24000d),
                    (phoebe, 10.8e6, 15.1e6), (proteus, 115000d, 120000d), (nereid, 1.3e6, 9.7e6)
                })
            {
                var body = NaturalBodyCatalogue.Find(id)!;
                if (!snapshot.TryState(id, out var child) || !snapshot.TryState(body.Parent, out var parent) ||
                    (child - parent).PositionKilometers.Length < minimum || (child - parent).PositionKilometers.Length > maximum ||
                    !snapshot.TryOrientation(id, out var frame) || Math.Abs(frame.ToWorld.Z.Length - 1) > 1e-12)
                    throw new InvalidOperationException($"The catalogue moon {id.Value} is missing or outside its expected range at {at}.");
            }
        }
        var a = Observer(phoebe, epoch, 440);
        var b = Observer(proteus, next, 890);
        string path = Path.Combine(directory, "catalogue-moons.stscene");
        ScenarioFile.Save(path, new([a, b], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != a || session[1] != b ||
            !scene.Renderer(0).PlanetGlobeTextured(phoebe) || !scene.Renderer(1).PlanetGlobeTextured(proteus))
            throw new InvalidOperationException("The original Phoebe and Proteus maps or independent poses did not survive reload.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the restored catalogue moons.");
        GD.Print("Catalogue moons: Phobos and Deimos fallback, Phoebe, Proteus and Nereid in modern and historical frames. Original maps visible in two restored views.");

        ObserverView Observer(ObjectId id, JulianInstant at, double range)
        {
            var snapshot = session.World.Graph.Evaluate(at);
            snapshot.TryState(id, out var state);
            Vector3d lit = (snapshot.SolarSystem!.Value.Sun - state).PositionKilometers;
            lit /= lit.Length;
            Vector3d eye = lit + Vector3d.Cross(lit, new(0, 0, 1)) * .28;
            eye /= eye.Length;
            Vector3d up = new Vector3d(0, 0, 1) - eye * eye.Z;
            up /= up.Length;
            return new(at, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(eye * range, eye * -1, up) }
            };
        }
    }
}
