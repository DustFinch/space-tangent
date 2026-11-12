// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMarsMoonModels(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_GPU") != "1")
            return;
        var session = scene.Session!;
        var snapshot = session.World.Graph.Evaluate(epoch);
        var phobos = new ObjectId("sol/phobos");
        var deimos = new ObjectId("sol/deimos");
        var a = Observer(phobos, 44);
        var b = Observer(deimos, 29);
        string path = Path.Combine(directory, "mars-moon-models.stscene");
        ScenarioFile.Save(path, new([a, b], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != a || session[1] != b)
            throw new InvalidOperationException("The original Mars moon views did not restore.");

        bool normalSource = System.Environment.GetEnvironmentVariable("SPACETANGENT_MOON_MODELS_NATIVE") == "1";
        Check(0, phobos, 49_152, a.Navigation!.Value.RangeKilometers, normalSource, false, 26.169);
        Check(1, deimos, 5_040, b.Navigation!.Value.RangeKilometers, false, true, 16.014);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the Mars moon models.");

        ObserverView Observer(ObjectId id, double range)
        {
            if (!snapshot.TryState(id, out var moon) || !snapshot.TryState(SolarSystemSample.SunId, out var sun))
                throw new InvalidOperationException($"The verified archive needs a state for {id.Value}.");
            Vector3d light = (sun - moon).PositionKilometers;
            light /= light.Length;
            Vector3d eye = light + Vector3d.Cross(light, new(0, 0, 1)) * .68;
            eye /= eye.Length;
            Vector3d up = new Vector3d(0, 0, 1) - eye * eye.Z;
            up /= up.Length;
            return new(epoch, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(moon.PositionKilometers, range, 0, 0, id, true)
                    { Placement = new NavigationPlacement(eye * range, eye * -1, up) }
            };
        }

        void Check(int view, ObjectId id, int triangles, double range, bool normal, bool diffuse, double width)
        {
            if (scene.Renderer(view).NaturalMoonModel(id) is not { Visible: true, Mesh: ArrayMesh mesh } node ||
                mesh.GetSurfaceCount() != 1 || mesh.SurfaceGetArrayLen(0) != triangles * 3 ||
                mesh.SurfaceGetMaterial(0) is not ShaderMaterial material ||
                material.GetShaderParameter("has_normal").AsBool() != normal ||
                material.GetShaderParameter("has_diffuse").AsBool() != diffuse ||
                Math.Abs(mesh.GetAabb().Size.X - width) > .001 ||
                Math.Abs(node.Basis.Scale.X * range / 48 - 1) > 1e-6)
                throw new InvalidOperationException($"{id.Value} lost raw coordinates, scale or texture bindings.");
            GD.Print($"Mars moon {id.Value}: {triangles:N0} triangles at raw kilometre scale, diffuse={diffuse}, normal={normal}.");
        }
    }
}
