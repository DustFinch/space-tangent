// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedAtmospheres(SolarSystemScene scene, string directory, string recordings)
    {
        var cases = new (string File, string Body, ObjectId Id)[]
        {
            ("venus", "Sol/Venus", SolarSystemSample.VenusId),
            ("mars", "Sol/Mars", PlanetCatalogue.MarsId),
            ("titan", "Sol/Saturn/Titan", new("sol/titan"))
        };
        string saved = Path.Combine(directory, "before-replay.stscene");
        if (!scene.SaveScenario(saved))
            throw new IOException("Could not save the live scenario.");

        foreach (var (file, name, id) in cases)
        {
            if (!scene.StartReplay(Path.Combine(recordings, file + ".celframe")))
                throw new InvalidOperationException($"Could not open the original {name} recording.");
            scene.SetProcess(false);
            try
            {
                var renderer = scene.Renderer(0);
                var geometry = renderer.ArchivedGeometry!;
                var recorded = geometry.Bodies.Single(body => body.Name == name);
                var body = renderer.ArchivedBody(name) ?? throw new InvalidOperationException($"The archived {name} body was not drawn.");
                var shell = renderer.ArchivedAtmosphere(id) ?? throw new InvalidOperationException($"The archived {name} atmosphere was not drawn.");
                var material = (ShaderMaterial)shell.MaterialOverride;
                var scattering = material.NextPass as ShaderMaterial ?? material;
                double height = (recorded.AtmosphereRadius - recorded.Radius) * geometry.KilometersPerUnit;
                if (!shell.Visible || height <= 0 || Math.Abs(scattering.GetShaderParameter("shell_height_km").AsSingle() - height) > .001 ||
                    Math.Abs(shell.Basis.X.Length() / body.Basis.X.Length() - recorded.AtmosphereRadius / recorded.Radius) > .00001 ||
                    shell.Position != body.Position ||
                    (file == "venus" ? !scattering.GetShaderParameter("venus_scattering").AsBool() :
                        !scattering.GetShaderParameter("use_atmosphere_lookup").AsBool()))
                    throw new InvalidOperationException($"The archived {name} shell did not use its recorded geometry and atmospheric profile.");

                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var air = renderer.GetViewport().GetTexture().GetImage();
                shell.Hide();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var bare = renderer.GetViewport().GetTexture().GetImage();
                int changed = 0;
                for (int y = 0; y < air.GetHeight(); y += 2)
                for (int x = 0; x < air.GetWidth(); x += 2)
                {
                    Color before = air.GetPixel(x, y), after = bare.GetPixel(x, y);
                    if (Math.Abs(before.R - after.R) + Math.Abs(before.G - after.G) + Math.Abs(before.B - after.B) > .025)
                        changed++;
                }
                if (changed < 25 || air.SavePng(Path.Combine(directory, file + "-replay.png")) != Error.Ok)
                    throw new InvalidOperationException($"The archived {name} atmosphere changed only {changed} GPU samples.");
                GD.Print($"Archived atmosphere GPU: {name}, recorded shell {height:F2} km, {changed} changed samples.");
            }
            finally
            {
                scene.SetProcess(true);
            }

            if (!scene.LoadScenario(saved) || scene.Renderer(0).ArchivedAtmosphere(id)?.Visible == true)
                throw new InvalidOperationException($"The {name} replay shell persisted in the live view.");
        }
    }

    private async Task ExerciseArchivedBodyGeometry(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_BODY_GPU") is not ("1" or "only"))
            return;
        string archive = System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_RECORDING")!;
        string saved = Path.Combine(directory, "before-archived-geometry.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(archive))
            throw new InvalidOperationException("Could not open the original geometry recording.");
        var renderer = scene.Renderer(0);
        var camera = renderer.NavigationCameraNode;
        if (renderer.ArchivedGeometry is not { } geometry ||
            renderer.ArchivedBody("Sol/Earth/Moon") is not { Visible: true } moon)
            throw new InvalidOperationException("The original recorded lunar body is unavailable.");

        var record = geometry.Bodies.Single(body => body.Name == "Sol/Earth/Moon");
        Vector3 local = camera.Basis.Inverse() * (moon.Position - camera.Position);
        double scale = moon.Basis.X.Length() / record.Radius;
        if ((local / (float)scale).DistanceTo(new((float)record.CameraPosition.X, (float)record.CameraPosition.Y,
            (float)record.CameraPosition.Z)) > .0002f)
            throw new InvalidOperationException("The rendered lunar geometry diverged from the recorded transform.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = renderer.GetViewport().GetTexture().GetImage();
        Vector2 center = camera.UnprojectPosition(moon.Position);
        Color disk = image.GetPixel((int)center.X, (int)center.Y);
        if (disk.R + disk.G + disk.B < .2f)
            throw new InvalidOperationException("The recorded lunar disc is absent at its projected center.");
        if (image.SavePng(Path.Combine(directory, "archived-lunar-geometry.png")) != Error.Ok)
            throw new IOException("Could not capture the recorded lunar geometry.");
        if (!scene.LoadScenario(saved) || renderer.ArchivedBody("Sol/Earth/Moon") is not null)
            throw new InvalidOperationException("Leaving replay did not restore live geometry.");
        GD.Print("Archived geometry GPU: rendered the recorded lunar transform and visible disc, then restored the live view.");
        string? orbits = System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_ORBITS");
        if (orbits is null)
            return;
        if (!scene.StartReplay(orbits) || renderer.ArchivedGeometry is not { Orbits.Count: 66, Labels.Count: 115 } ||
            renderer.ArchivedOrbitSurfaces != 66 || !renderer.ArchivedLabels.Any(label => label.Visible && label.Text == "Альтаир"))
            throw new InvalidOperationException("The original Windows recording did not publish its saved orbits and Unicode labels.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var overlays = renderer.GetViewport().GetTexture().GetImage();
        if (renderer.ArchivedBody("Sol/Earth")?.MaterialOverride is not ShaderMaterial earth ||
            !earth.GetShaderParameter("has_map").AsBool() || !earth.GetShaderParameter("has_night_map").AsBool() ||
            !earth.GetShaderParameter("has_cloud_map").AsBool())
            throw new InvalidOperationException("The replay Earth did not bind day, night and cloud maps.");
        var savedEarth = renderer.ArchivedGeometry!.Bodies.Single(body => body.Name == "Sol/Earth");
        var savedLight = savedEarth.LightColor;
        if (earth.GetShaderParameter("light_color").AsVector3().DistanceTo(
            new((float)savedLight.X, (float)savedLight.Y, (float)savedLight.Z)) > .000001f)
            throw new InvalidOperationException("The replay material did not retain the recorded light color.");
        GD.Print("Archived Earth material GPU: bound day, night and cloud maps and applied recorded light colors.");
        var symbols = renderer.ArchivedSymbols;
        if (symbols.Count == 0)
            throw new InvalidOperationException("The original recording has no projected deep-sky symbols.");
        GD.Print($"Archived symbols GPU: {symbols.Count} symbols ({symbols.Count(symbol => symbol.Square)} squares), diameter {symbols.Max(symbol => symbol.Diameter):F2} pixels.");
        if (overlays.SavePng(Path.Combine(directory, "archived-orbits-labels.png")) != Error.Ok)
            throw new IOException("Could not capture the saved orbit and label overlay.");
        if (renderer.ArchivedEarthLimb is not { Visible: true, MaterialOverride: ShaderMaterial air } limb ||
            air.GetShaderParameter("shell_height_override_km").AsSingle() is < 71.8f or > 72f)
            throw new InvalidOperationException("The replay atmosphere did not preserve the archived shell height.");
        scene.SetProcess(false);
        int atmospherePixels = 0;
        try
        {
            limb.Hide();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var withoutAir = renderer.GetViewport().GetTexture().GetImage();
            var earthCenter = camera.UnprojectPosition(renderer.ArchivedBody("Sol/Earth")!.Position);
            for (int y = Math.Max(0, (int)earthCenter.Y - 260); y < Math.Min(overlays.GetHeight(), earthCenter.Y + 260); y++)
            for (int x = Math.Max(0, (int)earthCenter.X - 260); x < Math.Min(overlays.GetWidth(), earthCenter.X + 260); x++)
            {
                Color with = overlays.GetPixel(x, y), without = withoutAir.GetPixel(x, y);
                if (with.B - without.B > .035f && with.B - without.B > with.R - without.R + .02f)
                    atmospherePixels++;
            }
        }
        finally
        {
            scene.SetProcess(true);
        }
        if (atmospherePixels < 30)
            throw new InvalidOperationException($"Expected the saved atmosphere to change the image, found {atmospherePixels} pixels.");
        GD.Print($"Archived Earth atmosphere GPU: recorded shell height 71.9 km, {atmospherePixels} blue limb pixels.");
        if (!scene.LoadScenario(saved) || renderer.ArchivedOrbitSurfaces != 0 || renderer.ArchivedLabels.Count != 0 || renderer.ArchivedSymbols.Count != 0)
            throw new InvalidOperationException("The saved orbit and label overlays persisted outside replay.");
        GD.Print("Archived overlays GPU: original Windows capture published 66 orbit strips and 115 labels including Cyrillic, then cleared them on exit.");
    }
}
