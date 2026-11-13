// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMeteorRadiants(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var epoch = TimeConversion.FromUtc(new(new(2026, 8, 12), 22, 30), TimeScale.Tdb);
        var solar = session.World.Ephemeris.Sample(epoch);
        var site = EarthObserverCamera.Moscow.Site;
        var light = EarthSurfaceLighting.Sample(solar, site, session.DeltaT);
        var sky = BrightSkyData.Load().Meteors;
        var radiant = sky.Radiants(solar, light, 6.5).Single(item => item.Shower.Code == "PER");
        var at = EarthObserving.HorizontalDirection(light, radiant.Direction)!.Value;
        var camera = new EarthObserverCamera(site, at.AzimuthDegrees!.Value, at.AltitudeDegrees);
        var observing = new ObserverView(epoch, CameraMode.Surface, null, false)
        {
            EarthObserver = camera,
            Sky = new SkyLayerSettings { Meteors = true }
        };
        var noLayer = observing with { Sky = default };
        string path = Path.Combine(directory, "perseid-radiant.stscene");
        ScenarioFile.Save(path, new([observing, noLayer], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !scene.Renderer(0).ShowsMeteorRadiant("PER") || scene.Renderer(1).ShowsMeteorRadiant("PER"))
            throw new InvalidOperationException("The Perseid radiant must be visible in only the enabled nighttime view.");
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var first = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            using var second = scene.Renderer(1).GetViewport().GetTexture().GetImage();
            using var full = GetViewport().GetTexture().GetImage();
            if (WarmPixels(first) <= WarmPixels(second) + 12 ||
                full.SavePng(Path.Combine(directory, "perseid-radiant.png")) != Error.Ok)
                throw new InvalidOperationException("The GPU did not draw the Perseid radiant overlay.");
        }
        scene.Seek(0, epoch.AddSeconds(43_200));
        if (scene.Renderer(0).ShowsMeteorRadiant("PER"))
            throw new InvalidOperationException("The meteor layer must be invisible in daylight.");
        GD.Print("Meteor radiants: original Perseid parameters, GPU marker in one view, daytime suppression.");

        string profile = Path.Combine(directory, "meteor-settings.json");
        File.WriteAllText(profile, """{"Site":{},"Meteors":false,"Radiants":true,"MeteorBoost":100}""");
        ObserverView markers = LegacyObserverProfiles.Read(profile, observing, session.DeltaT).Observer;
        File.WriteAllText(profile, """{"Site":{},"Meteors":true,"Radiants":false,"MeteorBoost":100}""");
        ObserverView trails = LegacyObserverProfiles.Read(profile, observing, session.DeltaT).Observer;
        ScenarioFile.Save(path, new([markers, trails], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || session[0] != markers || session[1] != trails ||
            !scene.Renderer(0).ShowsMeteorRadiant("PER") || scene.Renderer(1).ShowsMeteorRadiant("PER"))
            throw new InvalidOperationException("Independent archived Perseid settings must survive opening a scenario.");
        int trailCount = 0;
        for (int seconds = 10; seconds <= 60; seconds += 10)
        {
            scene.Seek(0, epoch.AddSeconds(seconds));
            scene.Seek(1, epoch.AddSeconds(seconds));
            trailCount = Math.Max(trailCount, scene.Renderer(1).ActiveMeteorTrailCount);
        }
        if (trailCount == 0 || scene.Renderer(0).ActiveMeteorTrailCount != 0 || !await scene.ReloadAddOns() ||
            session[0].Sky != markers.Sky || session[1].Sky != trails.Sky)
            throw new InvalidOperationException("Only the trail observer must spawn meteors, and both settings must survive reload.");
        GD.Print($"Meteor settings: independent original Perseid radiants and boosted trails ({trailCount} active).");

        static int WarmPixels(Image image)
        {
            Vector2I center = image.GetSize() / 2;
            int count = 0;
            for (int y = center.Y - 12; y < center.Y + 12; y++)
            for (int x = center.X - 12; x < center.X + 34; x++)
            {
                Color color = image.GetPixel(x, y);
                if (color.R > .3f && color.R > color.B + .1f)
                    count++;
            }
            return count;
        }
    }
}
