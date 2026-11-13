// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseTlePhotometry(SolarSystemScene scene, string directory)
    {
        var world = scene.Session!.World;
        var iss = world.FindLocalSatellite(new("norad/25544")) ??
            throw new InvalidOperationException("The original ISS TLE is required.");
        var fregat = world.FindLocalSatellite(new("norad/49271")) ??
            throw new InvalidOperationException("The original FREGAT TLE is required.");
        await CheckSatellitePhotometry(scene, directory, iss.Id, iss.Epoch, "iss-photometry", .005, .3, -2.5);
        await CheckSatellitePhotometry(scene, directory, fregat.Id, fregat.Epoch, "fregat-photometry", .005, .3);
        var jason = world.Satellites.Single(track => track.Id == new ObjectId("norad/26997"));
        await CheckSatellitePhotometry(scene, directory, jason.Id, jason.Epoch, "jason-photometry", .01, .5);
        GD.Print("Science photometry GPU: original Jason-1 catalogue radius, selectable point and independent eclipse.");
        GD.Print("TLE photometry GPU: original ISS -2.5 at 1000 km half lit, selectable point and independent eclipse.");
        GD.Print("TLE photometry GPU: original FREGAT debris uses archived default radius and albedo, with Earth shadow.");
    }

    private async Task CheckSatellitePhotometry(SolarSystemScene scene, string directory, ObjectId id, JulianInstant epoch,
        string sceneName, double legacyRadius, double legacyAlbedo, double? standardMagnitude = null)
    {
        var session = scene.Session!;
        const double range = 1000;
        ObserverView? day = null, night = null;
        double diffuseMagnitude = double.NaN;
        for (int minute = 0; minute <= 120 && (day is null || night is null); minute++)
        {
            JulianInstant instant = epoch.AddSeconds(minute * 60);
            var snapshot = session.World.Graph.Evaluate(instant);
            if (!snapshot.TryState(id, out var station) || snapshot.SolarSystem is not { } solar)
                continue;
            Vector3d fromEarth = (station - solar.Earth).PositionKilometers;
            Vector3d toSun = (solar.Sun - station).PositionKilometers;
            double sunDistance = toSun.Length;
            toSun /= sunDistance;
            double along = Vector3d.Dot(fromEarth, toSun);
            bool shadow = along < 0 && (fromEarth - toSun * along).Length < EarthSurfaceLighting.EarthRadiusKilometers - 50;
            var tangent = Vector3d.Cross(toSun, fromEarth);
            if (tangent.Length <= 0)
                continue;
            Vector3d direction = tangent / tangent.Length;
            Vector3d up = toSun;
            var pose = new NavigationCamera(station.PositionKilometers, range, 0, 0, id, true)
            {
                Placement = new NavigationPlacement(direction * range, direction * -1, up)
            };
            var view = new ObserverView(instant, CameraMode.Solar, id, false) { Navigation = pose };
            if (shadow)
                night ??= view;
            else if (along > 0 && day is null)
            {
                day = view;
                double ratio = legacyRadius / range * (149_597_870.7 / sunDistance);
                diffuseMagnitude = -26.74 - 2.5 * Math.Log10(legacyAlbedo * .5 * ratio * ratio);
            }
        }
        if (day is not { } illuminated || night is not { } eclipsed)
            throw new InvalidOperationException("The satellite orbit must provide both sunlit and eclipsed epochs.");
        string path = Path.Combine(directory, sceneName + ".stscene");
        var labels = new SkyLayerSettings(false, true, false, false);
        ScenarioFile.Save(path, new([illuminated with { Sky = labels }, eclipsed], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the two satellite observers.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        scene.Seek(0, illuminated.Epoch);
        scene.Seek(1, eclipsed.Epoch);
        var renderer = scene.Renderer(0);
        if (renderer.BodyPoint(id) is not { Visible: true, MaterialOverride: ShaderMaterial material } point ||
            Math.Abs(material.GetShaderParameter("magnitude").AsSingle() - (standardMagnitude ?? diffuseMagnitude)) > 1e-4 ||
            scene.Renderer(1).BodyPoint(id)?.Visible == true || scene.Renderer(1).LocalSatelliteVisible(id) ||
            scene.Renderer(1).ScienceBodyNode(id)?.Visible == true)
            throw new InvalidOperationException("The satellite must match its archived brightness at 1000 km and disappear in Earth's shadow.");
        if (renderer.BodyLabel(id) is not { Visible: true } || scene.Renderer(1).BodyLabel(id)?.Visible == true)
            throw new InvalidOperationException("The selected satellite label must appear only in the enabled view.");
        if (renderer.PickObject(renderer.NavigationCameraNode.UnprojectPosition(point.Position)) != id)
            throw new InvalidOperationException("The sunlit satellite point must remain selectable.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the two original satellite states.");

        if (renderer.ScienceBodyNode(id) is null)
            return;
        var farPose = illuminated.Navigation!.Value;
        var farPlacement = farPose.Placement!.Value;
        const double nearRange = .08;
        var nearPose = farPose with
        {
            RangeKilometers = nearRange,
            Placement = farPlacement with { OffsetKilometers = farPlacement.OffsetKilometers * (nearRange / range) }
        };
        string nearPath = Path.Combine(directory, sceneName + "-near.stscene");
        ScenarioFile.Save(nearPath, new([illuminated with { Sky = labels }, illuminated with { Navigation = nearPose }],
            false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(nearPath) || !await scene.ReloadAddOns() ||
            scene.Renderer(0).BodyPoint(id)?.Visible != true || scene.Renderer(0).ScienceBodyNode(id)?.Visible == true ||
            scene.Renderer(1).ScienceBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } || mesh.GetSurfaceCount() != 28 ||
            scene.Renderer(1).BodyPoint(id)?.Visible == true)
            throw new InvalidOperationException("Reload must preserve the Jason-1 point and its original textured model in separate views.");
    }
}
