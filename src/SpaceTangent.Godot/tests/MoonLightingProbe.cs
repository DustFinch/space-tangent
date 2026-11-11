// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class MoonLightingProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            string output = OS.GetEnvironment("SPACETANGENT_MOON_OUTPUT");
            Directory.CreateDirectory(output);
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            var startup = System.Diagnostics.Stopwatch.StartNew();
            while (!scene.InitializationFinished)
            {
                Require(startup.Elapsed.TotalSeconds < 20, "World initialization timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            string settings = Path.Combine(output, "probe-delta-t.txt");
            string? previousSettings = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH");
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", settings);
            try
            {
                scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", previousSettings);
            }

            scene.ShowFullMoon(0);
            SimulationViewport renderer = scene.Renderer(0);
            using Image nightOn = await Capture(renderer, output, "night-on", true, true);
            using Image nightOff = await Capture(renderer, output, "night-off", true, false);
            using Image noCloudOn = await Capture(renderer, output, "no-cloud-on", false, true);
            using Image noCloudOff = await Capture(renderer, output, "no-cloud-off", false, false);
            JulianInstant dayEpoch = SurfaceProjection.FullMoonEpoch.AddSeconds(43_200);
            scene.Seek(0, dayEpoch);
            using Image dayOn = await Capture(renderer, output, "day-on", true, true);
            using Image dayOff = await Capture(renderer, output, "day-off", true, false);
            using Image daySky = await Capture(renderer, output, "day-sky", false, false);
            JulianInstant twilightEpoch = FindEveningTwilight(scene);
            scene.Seek(0, twilightEpoch);
            using Image twilight = await Capture(renderer, output, "twilight", false, false);
            const double HorizonPitch = 5;
            renderer.SetSurfacePitch(HorizonPitch);
            scene.Seek(0, twilightEpoch);
            using Image horizon = await Capture(renderer, output, "twilight-horizon", false, false);
            double daySkyError = CompareAtmosphere(daySky, Sample(scene, dayEpoch));
            double twilightSkyError = CompareAtmosphere(twilight, Sample(scene, twilightEpoch));
            double horizonSkyError = CompareAtmosphere(horizon, Sample(scene, twilightEpoch), HorizonPitch);

            var (nightDifference, cloudContrast) = Difference(nightOn, nightOff);
            var (noCloudDifference, _) = Difference(noCloudOn, noCloudOff);
            var (dayDifference, _) = Difference(dayOn, dayOff);
            int moonPixels = CountBrightPixels(noCloudOn);
            Require(nightDifference > .005, "Lunar light did not brighten night clouds.");
            Require(cloudContrast > .005, "Night lighting should resolve spatial variation in the clouds.");
            Require(noCloudDifference < .00001, "Lunar cloud control changed pixels with clouds disabled.");
            Require(dayDifference < .00001, "Lunar cloud control changed daylight pixels.");
            Require(moonPixels > 100, "The full-Moon disc is not visible and resolved.");
            Require(daySkyError < .02 && twilightSkyError < .02 && horizonSkyError < .02,
                $"Sky RGB differs from the numerical reference. Day {daySkyError:F4}, twilight {twilightSkyError:F4}, horizon {horizonSkyError:F4}.");
            Color dayColor = daySky.GetPixel(daySky.GetWidth() / 2, daySky.GetHeight() / 2);
            Color twilightColor = twilight.GetPixel(twilight.GetWidth() / 2, twilight.GetHeight() / 2);
            Color horizonColor = horizon.GetPixel(horizon.GetWidth() / 2, 3 * horizon.GetHeight() / 4);
            Require(dayColor.B > dayColor.R + .1f && twilightColor.B < dayColor.B / 2 &&
                horizonColor.R > horizonColor.B,
                $"Expected blue daylight, dim twilight and a warm horizon. Horizon RGB {horizonColor}.");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                status = "passed",
                epochTdb = 2460600.5008,
                dayTdb = 2460601.0008,
                heading = 237,
                pitch = 28,
                verticalFieldOfView = 10,
                nightDifference,
                cloudContrast,
                noCloudDifference,
                dayDifference,
                moonPixels,
                twilightTdb = twilightEpoch.ToJulianDate(),
                twilightSolarElevationDegrees = SolarElevation(Sample(scene, twilightEpoch)),
                daySkyError,
                twilightSkyError,
                horizonSkyError
            }, new JsonSerializerOptions { WriteIndented = true }));

            GD.Print("SpaceTangent full Moon cloud rendering probe passed");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private static SurfaceLighting Sample(SolarSystemScene scene, JulianInstant epoch) =>
        EarthSurfaceLighting.Sample(scene.Session!.World.Ephemeris.Sample(epoch), new EarthSite(55.7558, 37.6173, .15),
            scene.Session.DeltaT);

    private static double SolarElevation(SurfaceLighting lighting)
    {
        Vector3d sun = lighting.EarthToSun - lighting.PositionKilometers;
        return Math.Asin(Vector3d.Dot(lighting.EastNorthUp.Z, sun) / sun.Length) * (180 / Math.PI);
    }

    private static JulianInstant FindEveningTwilight(SolarSystemScene scene)
    {
        JulianInstant noon = SurfaceProjection.FullMoonEpoch.AddSeconds(43_200);
        for (int minutes = 0; minutes < 720; minutes++)
        {
            JulianInstant time = noon.AddSeconds(minutes * 60);
            if (SolarElevation(Sample(scene, time)) <= -2.5)
                return time;
        }
        throw new InvalidOperationException("The Earth-surface test could not find evening twilight.");
    }

    private static double CompareAtmosphere(Image image, SurfaceLighting lighting, double pitch = 28)
    {
        double error = 0;
        foreach (int y in new[] { image.GetHeight() / 4, image.GetHeight() / 2, 3 * image.GetHeight() / 4 })
        {
            int x = image.GetWidth() / 2;
            Vector3d expected = EarthSkyReference.Pixel(lighting, image.GetWidth(), image.GetHeight(), x, y, pitch);
            Color actual = image.GetPixel(x, y);
            error = Math.Max(error, Math.Max(Math.Abs(expected.X - actual.R),
                Math.Max(Math.Abs(expected.Y - actual.G), Math.Abs(expected.Z - actual.B))));
        }
        return error;
    }

    private async Task<Image> Capture(SimulationViewport renderer, string output, string label, bool clouds, bool lunar)
    {
        renderer.SetSurfaceControls(clouds, lunar);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = renderer.GetViewport().GetTexture().GetImage();
        Require(image.GetWidth() >= 320 && image.GetHeight() >= 240, "Surface viewport is too small.");
        Error error = image.SavePng(Path.Combine(output, label + ".png"));
        Require(error == Error.Ok, "Could not save the comparison image.");
        return image;
    }

    private static (double Mean, double Contrast) Difference(Image on, Image off)
    {
        double difference = 0, squares = 0;
        for (int y = 0; y < on.GetHeight(); y++)
        for (int x = 0; x < on.GetWidth(); x++)
        {
            double delta = Math.Abs(on.GetPixel(x, y).R - off.GetPixel(x, y).R);
            difference += delta;
            squares += delta * delta;
        }
        double pixels = on.GetWidth() * on.GetHeight();
        double mean = difference / pixels;
        return (mean, Math.Sqrt(Math.Max(0, squares / pixels - mean * mean)));
    }

    private static int CountBrightPixels(Image image)
    {
        int count = 0;
        for (int y = 0; y < image.GetHeight(); y++)
        for (int x = 0; x < image.GetWidth(); x++)
        {
            if (image.GetPixel(x, y).R > .25f)
                count++;
        }
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
