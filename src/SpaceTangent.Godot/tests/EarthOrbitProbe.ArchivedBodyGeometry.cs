// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedShadows(SolarSystemScene scene, string directory, string recording)
    {
        string saved = Path.Combine(directory, "before-archived-shadows.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(recording))
            throw new InvalidOperationException("Could not replay the original solar eclipse.");
        scene.SetProcess(false);
        try
        {
            var renderer = scene.Renderer(0);
            var geometry = renderer.ArchivedGeometry!;
            var shadow = geometry.Shadows.Single(item => item.Receiver == "Sol/Earth");
            double radiusKm = shadow.Radius * geometry.KilometersPerUnit;
            if (radiusKm is < 1736 or > 1739 ||
                renderer.ArchivedBody("Sol/Earth")?.MaterialOverride is not ShaderMaterial material ||
                material.GetShaderParameter("archived_shadow_count").AsInt32() != 1)
                throw new InvalidOperationException("The Moon's recorded shadow geometry was not bound to Earth.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var eclipsed = renderer.GetViewport().GetTexture().GetImage();
            material.SetShaderParameter("archived_shadow_count", 0);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var lit = renderer.GetViewport().GetTexture().GetImage();
            if (renderer.ArchivedEarthLimb?.MaterialOverride is not ShaderMaterial air ||
                air.GetShaderParameter("archived_shadow_count").AsInt32() != 1)
                throw new InvalidOperationException("The recorded lunar shadow was not bound to Earth's atmosphere.");
            air.SetShaderParameter("archived_shadow_count", 0);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var unshadowedAir = renderer.GetViewport().GetTexture().GetImage();
            int limbPixels = 0;
            float limbContrast = 0;
            for (int y = 0; y < lit.GetHeight(); y++)
            for (int x = 0; x < lit.GetWidth(); x++)
            {
                float change = unshadowedAir.GetPixel(x, y).Luminance - lit.GetPixel(x, y).Luminance;
                if (change > .008f)
                    limbPixels++;
                limbContrast = Math.Max(limbContrast, change);
            }
            if (recording.EndsWith("earth-limb.celframe", StringComparison.Ordinal) && limbPixels < 5)
                throw new InvalidOperationException($"The recorded shadow darkened only {limbPixels} atmosphere pixels with contrast {limbContrast:F4}.");
            GD.Print($"Archived atmosphere shadow GPU: Earth, {limbPixels} atmosphere pixels, contrast {limbContrast:F4}.");
            int pixels = 0;
            float darkest = 0;
            for (int y = 0; y < eclipsed.GetHeight(); y++)
            for (int x = 0; x < eclipsed.GetWidth(); x++)
            {
                float change = lit.GetPixel(x, y).Luminance - eclipsed.GetPixel(x, y).Luminance;
                if (change > .03f)
                    pixels++;
                darkest = Math.Max(darkest, change);
            }
            if ((!recording.EndsWith("earth-limb.celframe", StringComparison.Ordinal) && (pixels < 30 || darkest < .15f)) ||
                eclipsed.SavePng(Path.Combine(directory, "archived-eclipse.png")) != Error.Ok ||
                lit.SavePng(Path.Combine(directory, "archived-eclipse-control.png")) != Error.Ok)
                throw new InvalidOperationException($"The recorded Earth shadow changed {pixels} pixels with contrast {darkest:F3}.");
            if (unshadowedAir.SavePng(Path.Combine(directory, "archived-eclipse-air-control.png")) != Error.Ok)
                throw new IOException("Could not save the atmosphere control.");
            if (!scene.LoadScenario(saved) || renderer.ArchivedGeometry is not null)
                throw new InvalidOperationException("The recorded Earth shadow persisted outside replay.");
            GD.Print($"Archived eclipse GPU: original Moon radius {radiusKm:F2} km, {pixels} darkened pixels, contrast {darkest:F3}.");
        }
        finally
        {
            scene.SetProcess(true);
        }
    }

    private async Task ExerciseArchivedComets(SolarSystemScene scene, string directory, string recording)
    {
        string saved = Path.Combine(directory, "before-archived-comets.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(recording))
            throw new InvalidOperationException("Could not open the original comet recording.");
        scene.SetProcess(false);
        try
        {
            var renderer = scene.Renderer(0);
            var tails = renderer.ArchivedGeometry!.CometTails;
            var dust = tails.Single(tail => tail.Name == "Sol/C 2023 A3 (Tsuchinshan-ATLAS)");
            var ion = tails.Single(tail => tail.Name == dust.Name + "/ion");
            if (renderer.ArchivedCometCount != tails.Count || dust.ComaRadius <= 0 ||
                dust.Trail.Length <= .1 || ion.Trail.Length > .001 || dust.Color == ion.Color)
                throw new InvalidOperationException("The recording did not restore both distinct tails and the coma.");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var active = renderer.GetViewport().GetTexture().GetImage();
            renderer.HideArchivedComets();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var bare = renderer.GetViewport().GetTexture().GetImage();
            int pixels = 0;
            for (int y = 0; y < active.GetHeight(); y += 2)
            for (int x = 0; x < active.GetWidth(); x += 2)
            {
                Color on = active.GetPixel(x, y), off = bare.GetPixel(x, y);
                if (Math.Abs(on.R - off.R) + Math.Abs(on.G - off.G) + Math.Abs(on.B - off.B) > .025)
                    pixels++;
            }
            if (pixels < 50 || active.SavePng(Path.Combine(directory, "archived-comet.png")) != Error.Ok)
                throw new InvalidOperationException($"The recorded comet changed only {pixels} GPU samples.");
            if (!scene.LoadScenario(saved) || renderer.ArchivedCometCount != 0)
                throw new InvalidOperationException("The archived comet tails persisted outside replay.");
            GD.Print($"Archived comet GPU: C/2023 A3 dust and ion tails, coma, {pixels} changed samples.");
        }
        finally
        {
            scene.SetProcess(true);
        }
    }

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
                if (file == "saturn" && rings.MaterialOverride is ShaderMaterial ringMaterial)
                {
                    int recordedShadows = geometry.Shadows.Count(item => item.Receiver == name);
                    if (recordedShadows == 0 || ringMaterial.GetShaderParameter("archived_shadow_count").AsInt32() != recordedShadows)
                        throw new InvalidOperationException("The original Saturn shadows did not reach the ring material.");
                    rings.Show();
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var shadowedRings = renderer.GetViewport().GetTexture().GetImage();
                    ringMaterial.SetShaderParameter("archived_shadow_count", 0);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var litRings = renderer.GetViewport().GetTexture().GetImage();
                    int ringShadowPixels = 0;
                    float contrast = 0;
                    for (int y = 0; y < litRings.GetHeight(); y++)
                    for (int x = 0; x < litRings.GetWidth(); x++)
                    {
                        float difference = litRings.GetPixel(x, y).Luminance - shadowedRings.GetPixel(x, y).Luminance;
                        if (difference > .003f)
                            ringShadowPixels++;
                        contrast = Math.Max(contrast, difference);
                    }
                    GD.Print($"Archived ring shadow GPU: Saturn, {recordedShadows} recorded occluders, {ringShadowPixels} darkened pixels, contrast {contrast:F4}.");
                    var surface = (ShaderMaterial)globe.MaterialOverride;
                    if (!surface.GetShaderParameter("cast_ring_shadows").AsBool())
                        throw new InvalidOperationException("The recorded Saturn rings did not reach its globe material.");
                    surface.SetShaderParameter("cast_ring_shadows", false);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var unshadowedGlobe = renderer.GetViewport().GetTexture().GetImage();
                    int globeShadowPixels = 0;
                    for (int y = 0; y < litRings.GetHeight(); y++)
                    for (int x = 0; x < litRings.GetWidth(); x++)
                        if (unshadowedGlobe.GetPixel(x, y).Luminance - litRings.GetPixel(x, y).Luminance > .025f)
                            globeShadowPixels++;
                    if (globeShadowPixels < 100 ||
                        litRings.SavePng(Path.Combine(directory, "saturn-ring-shadow.png")) != Error.Ok ||
                        unshadowedGlobe.SavePng(Path.Combine(directory, "saturn-ring-shadow-control.png")) != Error.Ok)
                        throw new InvalidOperationException($"The rings darkened only {globeShadowPixels} pixels on the recorded Saturn globe.");
                    GD.Print($"Archived ring-to-globe shadow GPU: Saturn, {globeShadowPixels} darkened pixels.");
                    Vector3 sun = ringMaterial.GetShaderParameter("sun_in_body").AsVector3();
                    Vector3 inPlane = new Vector3(sun.X, 0, sun.Z).Normalized();
                    Vector3 blocker = 1.8f * inPlane + .5f * sun;
                    ringMaterial.SetShaderParameter("archived_blockers",
                        new Vector4[] { new(blocker.X, blocker.Y, blocker.Z, .12f), default, default, default });
                    ringMaterial.SetShaderParameter("archived_shadow_count", 1);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var ringControl = renderer.GetViewport().GetTexture().GetImage();
                    int controlledPixels = 0;
                    for (int y = 0; y < ringControl.GetHeight(); y++)
                    for (int x = 0; x < ringControl.GetWidth(); x++)
                        if (unshadowedGlobe.GetPixel(x, y).Luminance - ringControl.GetPixel(x, y).Luminance > .025f)
                            controlledPixels++;
                    if (controlledPixels < 50)
                        throw new InvalidOperationException($"The controlled occluder darkened only {controlledPixels} ring pixels.");
                    ringMaterial.SetShaderParameter("archived_shadow_count", 0);
                    GD.Print($"Archived ring receiver GPU: controlled occluder, {controlledPixels} darkened pixels on the original Saturn geometry.");
                }
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
            string eclipseRecording = Path.Combine(recordings, "titan-eclipse.celframe");
            bool testShadow = file == "titan" && File.Exists(eclipseRecording);
            if (!scene.StartReplay(testShadow ? eclipseRecording : Path.Combine(recordings, file + ".celframe")))
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
                if (testShadow)
                {
                    if (scattering.GetShaderParameter("archived_shadow_count").AsInt32() != 1)
                        throw new InvalidOperationException("Saturn's recorded shadow did not reach Titan's atmosphere.");
                    scattering.SetShaderParameter("archived_shadow_count", 0);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var unshadowed = renderer.GetViewport().GetTexture().GetImage();
                    int changedAir = 0;
                    for (int y = 0; y < air.GetHeight(); y += 2)
                    for (int x = 0; x < air.GetWidth(); x += 2)
                        if (unshadowed.GetPixel(x, y).Luminance - air.GetPixel(x, y).Luminance > .008f)
                            changedAir++;
                    if (changedAir < 25)
                        throw new InvalidOperationException($"Saturn's recorded shadow affected only {changedAir} atmosphere samples.");
                    GD.Print($"Archived atmosphere shadow GPU: Titan, {changedAir} atmosphere samples.");
                }
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
                if ((!testShadow && changed < 25) || air.SavePng(Path.Combine(directory, file + "-replay.png")) != Error.Ok)
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

    private async Task ExerciseArchivedStellarMaterial(SolarSystemScene scene, string directory, string recording)
    {
        scene.SetProcess(false);
        string saved = Path.Combine(directory, "before-archived-stellar-material.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(recording))
            throw new InvalidOperationException("Could not open the original stellar material recording.");
        var renderer = scene.Renderer(0);
        var geometry = renderer.ArchivedGeometry!;
        var body = geometry.Bodies.Where(body => body.MaterialId == "star/a").MaxBy(body => body.Radius);
        var node = renderer.ArchivedBody(body.Name);
        if (node?.MaterialOverride is not ShaderMaterial material ||
            !material.GetShaderParameter("emissive").AsBool() ||
            !material.GetShaderParameter("has_map").AsBool() ||
            !material.GetShaderParameter("tint_map").AsBool())
            throw new InvalidOperationException($"The recorded stellar surface was not bound for {body.Name}.");
        Color tint = material.GetShaderParameter("tint").AsColor();
        if (tint.B <= tint.G || tint.G <= tint.R)
            throw new InvalidOperationException("The recorded A-class surface lost its blue tint.");

        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var lit = renderer.GetViewport().GetTexture().GetImage();
        material.SetShaderParameter("emissive", false);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var dark = renderer.GetViewport().GetTexture().GetImage();
        int litSamples = 0;
        for (int y = 0; y < lit.GetHeight(); y += 16)
        for (int x = 0; x < lit.GetWidth(); x += 16)
        {
            Color pixel = lit.GetPixel(x, y);
            if (pixel.B > pixel.G && pixel.G > pixel.R && pixel.Luminance - dark.GetPixel(x, y).Luminance > .06f)
                litSamples++;
        }
        if (lit.SavePng(Path.Combine(directory, "archived-stellar-material.png")) != Error.Ok ||
            dark.SavePng(Path.Combine(directory, "archived-stellar-material-control.png")) != Error.Ok || litSamples < 200)
            throw new InvalidOperationException($"The recorded stellar disk lit only {litSamples} sampled pixels.");
        if (!scene.LoadScenario(saved) || node.Visible)
            throw new InvalidOperationException("The archived stellar disk persisted outside replay.");
        GD.Print($"Archived stellar material GPU: {body.Name}, {body.MaterialId}, {litSamples} lit samples.");
    }

    private async Task ExerciseArchivedStarPoints(SolarSystemScene scene, string directory, string recording)
    {
        scene.SetProcess(false);
        string saved = Path.Combine(directory, "before-archived-star-points.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(recording))
            throw new InvalidOperationException("Could not open the recorded discrete stars.");
        var renderer = scene.Renderer(0);
        var geometry = renderer.ArchivedGeometry!;
        var node = renderer.GetNode<MeshInstance3D>("ArchivedStarPoints");
        if (!node.Visible || geometry.StarPoints.Count != 3)
            throw new InvalidOperationException("The recording did not restore three discrete stars.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var withStars = renderer.GetViewport().GetTexture().GetImage();
        node.Hide();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var withoutStars = renderer.GetViewport().GetTexture().GetImage();
        var camera = renderer.NavigationCameraNode;
        var contrasts = new List<float>();
        foreach (var star in geometry.StarPoints)
        {
            var direction = star.CameraDirection;
            var position = camera.UnprojectPosition(camera.ToGlobal(
                new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z) * 100));
            float contrast = 0;
            for (int y = (int)position.Y - 2; y <= (int)position.Y + 2; y++)
            for (int x = (int)position.X - 2; x <= (int)position.X + 2; x++)
                contrast = Math.Max(contrast, withStars.GetPixel(x, y).Luminance - withoutStars.GetPixel(x, y).Luminance);
            contrasts.Add(contrast);
        }
        if (contrasts[0] <= contrasts[1] + .1f || contrasts[1] < .1f || contrasts[2] > .01f ||
            withStars.SavePng(Path.Combine(directory, "archived-star-points.png")) != Error.Ok)
            throw new InvalidOperationException($"Incorrect brightness or occlusion of recorded stars: {string.Join(", ", contrasts)}.");
        node.Show();
        if (!scene.LoadScenario(saved) || node.Visible || node.Mesh is not null)
            throw new InvalidOperationException("Discrete star geometry survived leaving the replay.");
        GD.Print($"Archived discrete stars GPU: two visible points and one behind the Moon. Contrast: {string.Join(", ", contrasts)}.");
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
