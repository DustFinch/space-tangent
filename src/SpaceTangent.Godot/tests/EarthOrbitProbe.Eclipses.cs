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
