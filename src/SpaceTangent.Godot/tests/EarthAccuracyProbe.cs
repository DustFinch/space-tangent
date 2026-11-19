// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

/// <summary>Real GPU regression; runs in an isolated desktop or an unattended display, with no input events.</summary>
public partial class EarthAccuracyProbe : Node
{
    public override async void _Ready()
    {
        try
        {
            if (DisplayServer.GetName() == "headless") throw new InvalidOperationException("This test requires a real renderer.");
            string output = System.Environment.GetEnvironmentVariable("SPACETANGENT_ACCURACY_OUTPUT")!;
            Directory.CreateDirectory(output);
            await ExerciseTextureLifetime();
            var ephemeris = De440Ephemeris.Load(System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
            var sample = ephemeris.Sample(SurfaceProjection.FullMoonEpoch);
            var textures = SurfaceProjection.LoadTextures() ?? throw new InvalidOperationException("Install the original surface maps.");
            var results = new List<object>();
            foreach (double latitude in new[] { 0d, 55.7558, 75d })
            foreach (bool night in new[] { false, true })
            {
                var viewport = new SubViewport { Size = new(320, 240), RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                AddChild(viewport);
                var projection = new SurfaceProjection(viewport, textures, null);
                var model = DeltaTModel.MorrisonStephenson2004;
                Basis3d earth = EarthOrientation.TerrestrialToWorld(sample.Epoch, model).ToWorld;
                Vector3d target = earth.Transpose().Transform((night ? sample.Moon : sample.Sun).PositionKilometers - sample.Earth.PositionKilometers);
                double longitude = Math.Atan2(target.Y, target.X) * 180 / Math.PI;
                var site = new EarthSite(latitude, longitude, .15);
                var lighting = EarthSurfaceLighting.Sample(sample, site, model);
                var horizontal = EarthObserving.HorizontalDirection(lighting,
                    (night ? sample.Moon : sample.Sun).PositionKilometers - sample.Earth.PositionKilometers - lighting.PositionKilometers)!.Value;
                var camera = new EarthObserverCamera(site, horizontal.AzimuthDegrees ?? 0, Math.Max(5, horizontal.AltitudeDegrees));
                if (!projection.Draw(true, sample, model, camera, fieldOfView: 90)) throw new InvalidOperationException("Surface draw failed.");
                var rect = viewport.GetChildren().OfType<CanvasLayer>().Single().GetChildren().OfType<ColorRect>().Single();
                var material = (ShaderMaterial)rect.Material;
                Vector3 atmosphereEye = material.GetShaderParameter("atmosphere_observer").AsVector3();
                if (Math.Abs(atmosphereEye.Length() - (EarthSurfaceLighting.EarthRadiusKilometers + site.HeightKilometers)) > .002)
                    throw new InvalidOperationException("The atmosphere must retain geodetic observer height at every latitude.");
                Basis inverse = material.GetShaderParameter("world_to_earth").AsBasis();
                Vector3 pole = inverse * ToVector(earth.Z);
                if (pole.DistanceTo(Vector3.Back) > 1e-5f) throw new InvalidOperationException("The cloud pole must use the full Earth basis.");
                using var lit = await Capture(viewport);
                lit.SavePng(Path.Combine(output, $"surface-{latitude:F0}-{(night ? "moon" : "day")}.png"));
                material.SetShaderParameter(night ? "lunar_cloud_light" : "show_atmosphere", false);
                using var control = await Capture(viewport);
                int changed = Changed(lit, control);
                if (changed < 20) throw new InvalidOperationException($"Missing {(night ? "moonlit clouds" : "daylight atmosphere")} at {latitude}: {changed} pixels.");
                results.Add(new { Latitude = latitude, Night = night, ChangedPixels = changed, HeightKm = atmosphereEye.Length() - EarthSurfaceLighting.EarthRadiusKilometers });
                viewport.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            // A narrow latitude marker must stay beneath the same Earth-fixed camera at a historical epoch.
            foreach (double jd in new[] { 2451545d, 2106215d, 2816795d })
            {
                var epoch = JulianInstant.FromJulianDate(jd, TimeScale.Tdb);
                var viewport = new SubViewport { Size = new(256, 256), OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                AddChild(viewport);
                using var image = Image.CreateEmpty(512, 256, false, Image.Format.Rgb8);
                for (int y = 0; y < image.GetHeight(); y++)
                for (int x = 0; x < image.GetWidth(); x++)
                    image.SetPixel(x, y, Math.Abs((.5 - (y + .5) / 256) * 180 - 60) < 1.5 ? Colors.Lime : Colors.Magenta);
                var globe = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 128, Rings = 64 } };
                viewport.AddChild(globe);
                var renderer = new EarthOrbitRenderer(globe, new(ImageTexture.CreateFromImage(image), null, null), null);
                renderer.Update(sample with { Epoch = epoch }, DeltaTModel.MorrisonStephenson2004, false, false);
                Basis3d earth = EarthOrientation.TerrestrialToWorld(epoch, DeltaTModel.MorrisonStephenson2004).ToWorld;
                Vector3 direction = ToGodot(earth.Transform(new(Math.Cos(Math.PI / 3), 0, Math.Sin(Math.PI / 3))));
                var camera = new Camera3D { Position = direction * 3, Fov = 35 };
                viewport.AddChild(camera);
                camera.LookAt(Vector3.Zero, ToGodot(earth.Z));
                camera.MakeCurrent();
                using var frame = await Capture(viewport);
                Color center = frame.GetPixel(128, 128);
                if (center.G <= 2 * Math.Max(center.R, center.B) || center.G < .01f)
                    throw new InvalidOperationException($"Latitude marker lost at JD {jd}: {center}.");
                frame.SavePng(Path.Combine(output, $"earth-basis-{jd:F0}.png"));
                results.Add(new { JulianDate = jd, Center = center.ToHtml() });
                viewport.QueueFree();
            }
            File.WriteAllText(Path.Combine(output, "gpu-results.json"), JsonSerializer.Serialize(new {
                Renderer = RenderingServer.GetVideoAdapterName(), Checks = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            await CaptureReviewScene(output);
            GD.Print("Earth accuracy GPU passed: equator/Moscow/polar day and full-Moon clouds, historical Earth-fixed UV.");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private async Task CaptureReviewScene(string output)
    {
        string? scenarioPath = System.Environment.GetEnvironmentVariable("SPACETANGENT_FINAL_SCENE");
        if (string.IsNullOrEmpty(scenarioPath)) return;
        var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
        AddChild(scene);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!scene.InitializationFinished)
        {
            if (timer.Elapsed.TotalSeconds > 30) throw new TimeoutException("The review scene did not initialize.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var session = scene.Session ?? throw new InvalidOperationException("The review scene needs its ephemeris.");
        if (session.ViewCount == 1) scene.ToggleSplit();
        session.Synchronize(false, 0);
        session.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
        var jason = session.World.Satellites.Single(body => body.Name == "Jason-1");
        session.Select(0, jason.Id);
        session.SetPlaying(0, false);
        if (!session.GoToSelection(0)) throw new InvalidOperationException("Jason-1 must be a flight target.");
        session.UpdateCamera(0, session.World.Graph.Evaluate(session[0].Epoch, session.DeltaT), 30);
        scene.ShowFullMoon(1);
        session.SelectObject(1, SolarSystemSample.MoonId);
        session.SetPlaying(1, false);
        var before = session.CaptureScenario(0);
        if (!await scene.ReloadAddOns()) throw new InvalidOperationException("Review scene reload failed.");
        var after = session.CaptureScenario(0);
        if (!before.Views.SequenceEqual(after.Views) || before.DeltaT != after.DeltaT || before.Synchronized != after.Synchronized)
            throw new InvalidOperationException("Reload changed the review cameras, epochs or Delta T.");
        Directory.CreateDirectory(Path.GetDirectoryName(scenarioPath)!);
        ScenarioFile.Save(scenarioPath, after);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        image.SavePng(Path.Combine(output, "jason-full-moon.png"));
        GD.Print("Final review scene saved: Jason-1, full Moon, independent epochs and unchanged cameras/Delta T after reload.");
    }

    private async Task<Image> Capture(SubViewport viewport)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        return viewport.GetTexture().GetImage();
    }

    private static int Changed(Image a, Image b)
    {
        int changed = 0;
        for (int y = 0; y < a.GetHeight(); y++)
        for (int x = 0; x < a.GetWidth(); x++)
            if (Math.Abs(a.GetPixel(x, y).Luminance - b.GetPixel(x, y).Luminance) > .001f) changed++;
        return changed;
    }
    private static Vector3 ToVector(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    private static Vector3 ToGodot(Vector3d v) => new((float)v.X, (float)v.Z, (float)-v.Y);
}
