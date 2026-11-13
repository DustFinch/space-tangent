// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseHistoricalStars(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TRANSIENT_ROOT") is not { Length: > 0 } root)
            return;
        var catalogue = TransientStarCatalogue.Load(root);
        await Check(new("transient/sn-1054a"), 2105215, "sn-1054");
        await Check(new("transient/v1500-cyg"), 2442651, "nova-cygni-1975");

        async Task Check(ObjectId id, double earlier, string name)
        {
            TransientStar star = catalogue.Find(id) ?? throw new InvalidOperationException($"Missing original event {id.Value}.");
            Vector3d forward = star.Direction;
            Vector3d right = Vector3d.Cross(forward, new(0, 0, 1));
            Vector3d up = Vector3d.Cross(right / right.Length, forward);
            var pose = new NavigationCamera(forward * 2.6e8, 20_000_000, 0, 0, null, false)
                { Placement = new(default, forward, up) };
            var before = new ObserverView(JulianInstant.FromJulianDate(earlier, TimeScale.Tdb), CameraMode.Solar, null, false)
                { Navigation = pose, FieldOfViewDegrees = 8, Sky = new(true, false, false, false) };
            var at = before with { Epoch = JulianInstant.FromJulianDate(star.PeakJulianDay, TimeScale.Tdb) };
            string path = Path.Combine(directory, name + ".stscene");
            var session = scene.Session!;
            if (before.Epoch.CompareTo(session.World.Ephemeris.Start) < 0 ||
                at.Epoch.CompareTo(session.World.Ephemeris.End) > 0)
                throw new InvalidOperationException($"The configured ephemeris does not cover both {star.Name} epochs. " +
                    "Set SPACETANGENT_VSOP87 to the historical planetary series.");
            ScenarioFile.Save(path, new([before, at], false, 0, 1, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException($"Could not open the two-epoch scenario for {star.Name}.");
            if (scene.Renderer(0).TransientStarVisible(id) || !scene.Renderer(1).TransientStarVisible(id))
                throw new InvalidOperationException($"The independent epochs must show {star.Name} only in the view at peak.");
            if (!await scene.ReloadAddOns() || scene.Renderer(0).TransientStarVisible(id) || !scene.Renderer(1).TransientStarVisible(id))
                throw new InvalidOperationException($"Reload must preserve both independent epochs of {star.Name}.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var off = scene.Renderer(0).GetViewport().GetTexture().GetImage();
                using var on = scene.Renderer(1).GetViewport().GetTexture().GetImage();
                using var frame = GetViewport().GetTexture().GetImage();
                float absent = CenterPeak(off), present = CenterPeak(on);
                if (present < .5f || present - absent < .3f)
                    throw new InvalidOperationException($"The event did not brighten the rendered center for {name}: {absent:F3} to {present:F3}.");
                if (frame.SavePng(Path.Combine(directory, name + ".png")) != Error.Ok)
                    throw new IOException($"Could not capture the two epochs for {name}.");
                GD.Print($"Historical brightness rendered: {name}, peak center {absent:F3} to {present:F3}.");
            }
        }

        static float CenterPeak(Image image)
        {
            Vector2I center = image.GetSize() / 2;
            float peak = 0;
            for (int x = -5; x <= 5; x++)
            for (int y = -5; y <= 5; y++)
            {
                Color pixel = image.GetPixel(center.X + x, center.Y + y);
                peak = Math.Max(peak, (pixel.R + pixel.G + pixel.B) / 3);
            }
            return peak;
        }
    }
}
