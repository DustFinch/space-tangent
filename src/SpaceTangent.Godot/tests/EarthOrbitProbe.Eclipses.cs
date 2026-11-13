// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseTitanEclipse(SolarSystemScene scene, string directory)
    {
        if (DisplayServer.GetName() == "headless" ||
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT")))
            return;
        var session = scene.Session!;
        var titan = new ObjectId("sol/titan");
        var start = JulianInstant.FromJulianDate(2455050.5, TimeScale.Tdb);
        for (int step = 0; step < 48 * 32; step++)
        {
            var epoch = start.AddSeconds(step * 1800);
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (snapshot.SolarSystem is not { } solar || !snapshot.TryState(titan, out var body) ||
                !snapshot.TryState(PlanetCatalogue.SaturnId, out var saturn))
                continue;
            var towardSun = (solar.Sun - body).PositionKilometers;
            var towardSaturn = (saturn - body).PositionKilometers;
            if (SolarOccultation.VisibleFraction(towardSun, towardSaturn, 54_364) > .01)
                continue;

            var direction = towardSun / towardSun.Length;
            var up = new Vector3d(0, 0, 1) - direction * direction.Z;
            const double range = 10500;
            var pose = new NavigationCamera(body.PositionKilometers, range, 0, 0, titan, true)
                { Placement = new(direction * range, direction * -1, up / up.Length) };
            var observer = new ObserverView(epoch, CameraMode.Solar, titan, false) { Navigation = pose };
            string path = Path.Combine(directory, "titan-saturn-eclipse.stscene");
            ScenarioFile.Save(path, new([observer], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path) || !scene.Renderer(0).WorldAtmosphereActive(titan, lookup: true))
                throw new InvalidOperationException("Titan eclipse requires the original atmosphere LUT.");
            var renderer = scene.Renderer(0);
            var materials = renderer.GetChildren().OfType<MeshInstance3D>()
                .SelectMany(node => node.GetChildren().OfType<MeshInstance3D>().Prepend(node))
                .Where(node => node.IsVisibleInTree())
                .Select(node => node.MaterialOverride).OfType<ShaderMaterial>()
                .Where(material => material.GetShaderParameter("occulting_radius_km").AsSingle() > 0).ToArray();
            if (materials.Length != 2)
                throw new InvalidOperationException("Saturn must cast a shadow on the Titan globe and limb.");
            using var dark = await Frame();
            try
            {
                foreach (var material in materials)
                    material.SetShaderParameter("occulting_radius_km", 0f);
                using var lit = await Frame();
                int dimmed = 0, limbDimmed = 0;
                double focal = dark.GetHeight() / (2 * Math.Tan(renderer.NavigationCameraNode.Fov * Math.PI / 360));
                double limbRadius = focal * 2574.73 / Math.Sqrt(range * range - 2574.73 * 2574.73);
                for (int y = 0; y < dark.GetHeight(); y += 3)
                for (int x = 0; x < dark.GetWidth(); x += 3)
                {
                    Color a = dark.GetPixel(x, y), b = lit.GetPixel(x, y);
                    double difference = b.R + b.G + b.B - a.R - a.G - a.B;
                    if (difference > .15)
                        dimmed++;
                    double dx = x + .5 - dark.GetWidth() * .5;
                    double dy = y + .5 - dark.GetHeight() * .5;
                    double distance = Math.Sqrt(dx * dx + dy * dy);
                    if (distance > limbRadius + 2 && distance < limbRadius + 30 && difference > .03)
                        limbDimmed++;
                }
                if (dimmed < 500 || limbDimmed < 50)
                    throw new InvalidOperationException($"Titan eclipse changed {dimmed} GPU samples and {limbDimmed} limb samples.");
                if (dark.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not save the Titan eclipse frame.");
                GD.Print(FormattableString.Invariant($"Titan eclipse GPU: Saturn dims {dimmed} samples, with {limbDimmed} samples outside the solid globe, at JD {epoch.ToJulianDate():F6}, original orbit and atmosphere LUT."));
            }
            finally
            {
                scene.Seek(0, epoch);
            }
            return;
        }
        throw new InvalidOperationException("The archived Saturn equinox must provide a central Titan eclipse.");

        async Task<Image> Frame()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return scene.Renderer(0).GetViewport().GetTexture().GetImage();
        }
    }

    private async Task ExerciseEclipses(SolarSystemScene scene, string directory)
    {
        var world = scene.Session!.World;
        var earth = SolarSystemSample.EarthId;
        var moon = SolarSystemSample.MoonId;
        double earthRadius = EarthSurfaceLighting.EarthRadiusKilometers;
        double moonRadius = EarthSurfaceLighting.MoonRadiusKilometers;
        var solarEpoch = FromTt(new(2024, 4, 8, 18, 18, 29));
        var solar = world.Ephemeris.Sample(solarEpoch);
        Vector3d sunward = (solar.Sun - solar.Earth).PositionKilometers;
        Vector3d moonward = (solar.Moon - solar.Earth).PositionKilometers;
        Vector3d axis = moonward - sunward;
        axis /= axis.Length;
        double closest = -Vector3d.Dot(moonward, axis);
        double miss = (moonward + axis * closest).Length;
        Vector3d umbra = moonward + axis * (closest - Math.Sqrt(earthRadius * earthRadius - miss * miss));
        using var earthImage = await Capture("earth-solar-eclipse", solarEpoch, earth, solar.Earth.PositionKilometers, umbra, earthRadius * 4.2);

        if (earthImage is not null)
            await CheckEarthLayers(earthImage);

        var lunarEpoch = FromTt(new(2025, 9, 7, 18, 12, 58));
        using var darkMoon = await CaptureMoon("moon-totality", lunarEpoch);
        using var brightMoon = await CaptureMoon("moon-after-totality", lunarEpoch.AddSeconds(JulianInstant.SecondsPerDay));
        if (earthImage is null || darkMoon is null || brightMoon is null)
            return;

        double shadow = MeanLuminance(earthImage, 0, 5);
        double adjacent = MeanLuminance(earthImage, 38, 58);
        double dark = MeanLuminance(darkMoon, 0, 25);
        double lit = MeanLuminance(brightMoon, 0, 25);
        GD.Print(FormattableString.Invariant($"Eclipse luminance: Earth shadow {shadow:F5}, surrounding disc {adjacent:F5}, Moon totality {dark:F5}, Moon control {lit:F5}."));
        if (shadow >= adjacent * .65 || dark >= lit * .4)
            throw new InvalidOperationException("The rendered eclipse umbrae must be substantially darker than their controls.");

        async Task CheckEarthLayers(Image eclipse)
        {
            var globe = scene.Renderer(0).EarthOrbitGlobe;
            if (globe.MaterialOverride is not ShaderMaterial ground ||
                globe.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(node =>
                    node.MaterialOverride is ShaderMaterial shader && shader.Shader.ResourcePath.EndsWith("earth_limb.gdshader", StringComparison.Ordinal)) is not { } limb ||
                limb.MaterialOverride is not ShaderMaterial air ||
                !ground.GetShaderParameter("solar_eclipse_active").AsBool() ||
                !air.GetShaderParameter("solar_eclipse_active").AsBool())
                throw new InvalidOperationException("The Earth orbit globe and limb must both receive the real eclipse geometry.");
            if (!ground.GetShaderParameter("has_cloud_map").AsBool() ||
                !ground.GetShaderParameter("use_atmosphere_lookup").AsBool())
            {
                if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ECLIPSE_PROBE") == "only")
                    throw new InvalidOperationException("The eclipse layer probe requires the original cloud map and atmosphere LUT.");
                return;
            }

            try
            {
                ground.SetShaderParameter("solar_eclipse_active", false);
                air.SetShaderParameter("solar_eclipse_active", false);
                using var lit = await LayerFrame();
                ground.SetShaderParameter("has_cloud_map", false);
                using var litNoClouds = await LayerFrame();
                ground.SetShaderParameter("solar_eclipse_active", true);
                air.SetShaderParameter("solar_eclipse_active", true);
                using var eclipseNoClouds = await LayerFrame();
                ground.SetShaderParameter("show_atmosphere", false);
                limb.Hide();
                using var eclipseNoAir = await LayerFrame();
                ground.SetShaderParameter("solar_eclipse_active", false);
                using var litNoAir = await LayerFrame();

                double eclipsedCloud = LayerBrightness(eclipse, eclipseNoClouds);
                double litCloud = LayerBrightness(lit, litNoClouds);
                double eclipsedAir = LayerBrightness(eclipseNoClouds, eclipseNoAir);
                double litAir = LayerBrightness(litNoClouds, litNoAir);
                GD.Print(FormattableString.Invariant($"Earth eclipse layers GPU: clouds {eclipsedCloud:F6}/{litCloud:F6}, air {eclipsedAir:F6}/{litAir:F6} (eclipse/unshadowed)."));
                if (litCloud < .001 || litAir < .001 || eclipsedCloud >= litCloud * .7 || eclipsedAir >= litAir * .7)
                    throw new InvalidOperationException("Earth's cloud and air contributions must respond independently to the real eclipse.");
            }
            finally
            {
                ground.SetShaderParameter("solar_eclipse_active", true);
                air.SetShaderParameter("solar_eclipse_active", true);
                ground.SetShaderParameter("has_cloud_map", true);
                ground.SetShaderParameter("show_atmosphere", true);
                limb.Show();
            }

            async Task<Image> LayerFrame()
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                return scene.Renderer(0).GetViewport().GetTexture().GetImage();
            }

            static double LayerBrightness(Image withLayer, Image withoutLayer)
            {
                const int radius = 9;
                int cx = withLayer.GetWidth() / 2, cy = withLayer.GetHeight() / 2;
                double sum = 0;
                for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                {
                    Color a = withLayer.GetPixel(cx + x, cy + y), b = withoutLayer.GetPixel(cx + x, cy + y);
                    sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                }
                return sum / ((2 * radius + 1) * (2 * radius + 1) * 3);
            }
        }

        async Task<Image?> CaptureMoon(string name, JulianInstant epoch)
        {
            var sample = world.Ephemeris.Sample(epoch);
            return await Capture(name, epoch, moon, sample.Moon.PositionKilometers,
                (sample.Sun - sample.Moon).PositionKilometers, moonRadius * 4.2);
        }

        async Task<Image?> Capture(string name, JulianInstant epoch, ObjectId target, Vector3d center, Vector3d outward, double range)
        {
            Vector3d direction = outward / outward.Length;
            var camera = new NavigationCamera(center, range, Math.Atan2(direction.X, -direction.Y),
                Math.Asin(direction.Z), target, true);
            string path = Path.Combine(directory, name + ".stscene");
            ScenarioFile.Save(path, new([new ObserverView(epoch, CameraMode.Solar, target, false) { Navigation = camera }],
                false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not restore the {name} eclipse camera.");
            if (DisplayServer.GetName() == "headless")
                return null;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var full = GetViewport().GetTexture().GetImage();
            if (full.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                throw new IOException($"Could not capture {name}.");
            return scene.Renderer(0).GetViewport().GetTexture().GetImage();
        }

        static double MeanLuminance(Image image, int inner, int outer)
        {
            int xCenter = image.GetWidth() / 2, yCenter = image.GetHeight() / 2;
            double sum = 0;
            int count = 0;
            for (int y = -outer; y <= outer; y++)
            for (int x = -outer; x <= outer; x++)
            {
                int distanceSquared = x * x + y * y;
                if (distanceSquared < inner * inner || distanceSquared > outer * outer)
                    continue;
                Color pixel = image.GetPixel(xCenter + x, yCenter + y);
                sum += .2126 * pixel.R + .7152 * pixel.G + .0722 * pixel.B;
                count++;
            }
            return sum / count;
        }

        static JulianInstant FromTt(DateTime date)
        {
            DateTime j2000 = new(2000, 1, 1, 12, 0, 0);
            return TimeConversion.Convert(new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tt)
                .AddNanoseconds((Int128)(date.Ticks - j2000.Ticks) * 100), TimeScale.Tdb);
        }
    }
}
