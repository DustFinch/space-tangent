// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedRings(SolarSystemScene scene, string directory, string recordings)
    {
        string saved = Path.Combine(directory, "before-archived-rings.stscene");
        if (!scene.SaveScenario(saved))
            throw new IOException("Could not save the live view.");
        foreach (var (file, name) in new[] { ("saturn", "Sol/Saturn"), ("uranus", "Sol/Uranus") })
        {
            if (!scene.StartReplay(Path.Combine(recordings, file + ".celframe")))
                throw new InvalidOperationException($"Could not open the original {name} recording.");
            scene.SetProcess(false);
            try
            {
                var renderer = scene.Renderer(0);
                var geometry = renderer.ArchivedGeometry!;
                var record = geometry.Bodies.Single(body => body.Name == name);
                var globe = renderer.ArchivedBody(name)!;
                var rings = renderer.ArchivedRings(record.Id!.Value);
                if (rings is not { Visible: true, Mesh: ImmediateMesh mesh } ||
                    record.RingInnerRadius <= record.Radius || record.RingOuterRadius <= record.RingInnerRadius ||
                    rings.Position != globe.Position || rings.Basis.Y.Normalized().DistanceTo(globe.Basis.Y.Normalized()) > .0001f ||
                    Math.Abs(mesh.GetAabb().Size.X / 2 - record.RingOuterRadius / record.Radius) > .0001)
                    throw new InvalidOperationException($"The saved radii or orientation of the {name} rings did not render.");
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var with = renderer.GetViewport().GetTexture().GetImage();
                rings.Hide();
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var without = renderer.GetViewport().GetTexture().GetImage();
                int changed = 0;
                for (int y = 0; y < with.GetHeight(); y += 2)
                for (int x = 0; x < with.GetWidth(); x += 2)
                    if (Math.Abs(with.GetPixel(x, y).Luminance - without.GetPixel(x, y).Luminance) > .025)
                        changed++;
                if (changed < 50 || with.SavePng(Path.Combine(directory, file + "-replay.png")) != Error.Ok)
                    throw new InvalidOperationException($"The {name} rings changed only {changed} GPU samples.");
                GD.Print($"Archived rings GPU: {name}, {record.RingInnerRadius * geometry.KilometersPerUnit:F1}–{record.RingOuterRadius * geometry.KilometersPerUnit:F1} km, {changed} changed samples.");
                if (file == "saturn")
                {
                    const string nameOfMoon = "Sol/Saturn/Atlas";
                    var savedPoint = geometry.BodyPoints[nameOfMoon];
                    var moon = renderer.ArchivedPoint(nameOfMoon);
                    if (!savedPoint.ReplacesSurface || renderer.ArchivedBody(nameOfMoon) is not null ||
                        moon is not { Visible: true, MaterialOverride: ShaderMaterial pointMaterial } ||
                        Math.Abs(pointMaterial.GetShaderParameter("magnitude").AsSingle() - savedPoint.Magnitude) > .0001)
                        throw new InvalidOperationException("Atlas did not use the archived unresolved-body photometry.");
                    rings.Show();
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var lit = renderer.GetViewport().GetTexture().GetImage();
                    moon.Hide();
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var control = renderer.GetViewport().GetTexture().GetImage();
                    Vector2 pixel = renderer.NavigationCameraNode.UnprojectPosition(moon.Position);
                    int pointPixels = 0;
                    for (int y = Math.Max(0, (int)pixel.Y - 15); y < Math.Min(lit.GetHeight(), (int)pixel.Y + 16); y++)
                    for (int x = Math.Max(0, (int)pixel.X - 15); x < Math.Min(lit.GetWidth(), (int)pixel.X + 16); x++)
                        if (lit.GetPixel(x, y).Luminance - control.GetPixel(x, y).Luminance > .025)
                            pointPixels++;
                    if (pointPixels < 3)
                        throw new InvalidOperationException($"Atlas changed only {pointPixels} GPU pixels at its recorded location.");
                    GD.Print($"Archived body point GPU: Atlas magnitude {savedPoint.Magnitude:F2}, {pointPixels} lit pixels and no surface mesh.");
                }
                if (!scene.LoadScenario(saved) || rings.Visible)
                    throw new InvalidOperationException($"The {name} rings persisted after replay.");
            }
            finally
            {
                scene.SetProcess(true);
            }
        }
    }

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
        if (renderer.ArchivedStarCount != 0 || !scene.StartReplay(archive) || renderer.ArchivedStarCount != 183247)
            throw new InvalidOperationException("The recorded background and moving stars did not replace the live field.");
        scene.SetProcess(false);
        try
        {
            var background = renderer.GetNode<MeshInstance3D>("ArchivedStarBackground");
            var moving = renderer.GetNode<MeshInstance3D>("ArchivedMovingStars");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var stars = renderer.GetViewport().GetTexture().GetImage();
            background.Hide();
            moving.Hide();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var blank = renderer.GetViewport().GetTexture().GetImage();
            int changed = 0;
            for (int y = 0; y < stars.GetHeight(); y++)
            for (int x = 0; x < stars.GetWidth(); x++)
                if (stars.GetPixel(x, y).Luminance - blank.GetPixel(x, y).Luminance > .025)
                    changed++;
            if (changed < 20 || stars.SavePng(Path.Combine(directory, "archived-stars.png")) != Error.Ok)
                throw new InvalidOperationException($"Recorded stars changed only {changed} pixels.");
            GD.Print($"Archived stars GPU: 182625 catalogue entries, 622 moving entries, {changed} lit pixels.");
        }
        finally
        {
            scene.SetProcess(true);
        }
        if (!scene.LoadScenario(saved) || renderer.ArchivedStarCount != 0)
            throw new InvalidOperationException("The archived stars persisted after replay.");
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
