// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionTiledGlobe(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_TILE_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_TILE_CORPUS") is not { Length: > 0 } root)
            return;
        const string input = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? installed = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-mission-tiled-globe.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observer before installing the tiled globe.");
        var session = scene.Session!;
        var target = new ObjectId("mission/mars-tile-witness");
        var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        byte[]? fullCenter = null;
        try
        {
            foreach (string format in new[] { "source", "packed", "sparse" })
            {
                System.Environment.SetEnvironmentVariable(input, Path.Combine(root, "p4-tiled-globe-" + format));
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException($"Could not install the {format} tiled globe.");
                var left = Observer(epoch, 4400);
                var right = Observer(epoch.AddSeconds(18 * 86400), 10_800);
                string path = Path.Combine(directory, $"mission-tiled-globe-{format}.stscene");
                var deltaT = session.DeltaT;
                ScenarioFile.Save(path, new([left, right], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != left || session[1] != right ||
                    session.DeltaT != deltaT)
                    throw new InvalidOperationException("Tiled globe cameras or Delta T changed at reload.");
                var first = Material(0);
                var second = Material(1);
                if (first == second || first.GetShaderParameter("light_in_body").AsVector3() == second.GetShaderParameter("light_in_body").AsVector3() ||
                    first.GetShaderParameter("native_base_pages").AsGodotObject() != second.GetShaderParameter("native_base_pages").AsGodotObject())
                    throw new InvalidOperationException("The tiled globe must share tile resources and keep independent lighting.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not capture the tiled mission globe.");
                using var view = scene.Renderer(0).GetViewport().GetTexture().GetImage();
                var center = view.GetRegion(new Rect2I(view.GetWidth() / 2 - 50, view.GetHeight() / 2 - 50, 100, 100));
                byte[] pixels = center.GetData();
                center.Dispose();
                if (format == "source")
                    fullCenter = pixels;
                else if (fullCenter is null || pixels.SequenceEqual(fullCenter) != (format == "packed"))
                    throw new InvalidOperationException($"Unexpected pixels for the {format} tile pyramid.");
                using var map = first.GetShaderParameter("native_base_pages").AsGodotObject() is Texture2D pages
                    ? pages.GetImage() : throw new InvalidOperationException("Missing tile page texture.");
                int count = 0;
                for (int y = 0; y < map.GetHeight(); y++)
                for (int x = 0; x < map.GetWidth(); x++)
                    if (map.GetPixel(x, y).A > 0)
                        count++;
                if (count != (format == "sparse" ? 2 : 170))
                    throw new InvalidOperationException($"Unexpected number of published color tiles: {count}.");
                GD.Print($"Mission tiled globe GPU ({format}): {count} color pages, DDS normals, two independent cameras and Delta T restored.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, installed);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior))
            throw new InvalidOperationException("Could not restore the original mission installation.");

        ObserverView Observer(JulianInstant at, double range)
        {
            var snapshot = session.World.Graph.Evaluate(at);
            if (!snapshot.TryState(target, out var state) || snapshot.SolarSystem is not { } solar)
                throw new InvalidOperationException("The tiled globe did not enter the world.");
            Vector3d outward = solar.Sun.PositionKilometers - state.PositionKilometers;
            outward /= outward.Length;
            var up = new Vector3d(0, 0, 1) - outward * outward.Z;
            return new(at, CameraMode.Solar, target, false)
            {
                Navigation = new NavigationCamera(state.PositionKilometers, range, 0, 0, target, true)
                    { Placement = new NavigationPlacement(outward * range, outward * -1, up / up.Length) }
            };
        }

        ShaderMaterial Material(int view)
        {
            if (scene.Renderer(view).ImportedBodyNode(target) is not { Visible: true, MaterialOverride: ShaderMaterial material } ||
                !material.GetShaderParameter("native_base_enabled").AsBool() ||
                !material.GetShaderParameter("native_normal_enabled").AsBool() ||
                material.GetShaderParameter("native_normal_encoding").AsInt32() != 1)
                throw new InvalidOperationException("The two DDS pyramids must be active on the imported globe.");
            return material;
        }
    }
}
