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
        var session = scene.Session!;
        var id = new ObjectId("norad/25544");
        var iss = session.World.FindLocalSatellite(id) ?? throw new InvalidOperationException("The original ISS TLE is required.");
        ObserverView? day = null, night = null;
        for (int minute = 0; minute <= 120 && (day is null || night is null); minute++)
        {
            JulianInstant instant = iss.Epoch.AddSeconds(minute * 60);
            var snapshot = session.World.Graph.Evaluate(instant);
            if (!snapshot.TryState(id, out var station) || snapshot.SolarSystem is not { } solar)
                continue;
            Vector3d fromEarth = (station - solar.Earth).PositionKilometers;
            Vector3d toSun = (solar.Sun - station).PositionKilometers;
            toSun /= toSun.Length;
            double along = Vector3d.Dot(fromEarth, toSun);
            bool shadow = along < 0 && (fromEarth - toSun * along).Length < EarthSurfaceLighting.EarthRadiusKilometers - 50;
            var tangent = Vector3d.Cross(toSun, fromEarth);
            if (tangent.Length <= 0)
                continue;
            Vector3d direction = tangent / tangent.Length;
            Vector3d up = toSun;
            var pose = new NavigationCamera(station.PositionKilometers, 1000, 0, 0, id, true)
            {
                Placement = new NavigationPlacement(direction * 1000, direction * -1, up)
            };
            var view = new ObserverView(instant, CameraMode.Solar, id, false) { Navigation = pose };
            if (shadow)
                night ??= view;
            else if (along > 0)
                day ??= view;
        }
        if (day is not { } illuminated || night is not { } eclipsed)
            throw new InvalidOperationException("The ISS orbit must provide both sunlit and eclipsed epochs.");
        string path = Path.Combine(directory, "iss-photometry.stscene");
        ScenarioFile.Save(path, new([illuminated, eclipsed], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the two ISS observers.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        scene.Seek(0, illuminated.Epoch);
        scene.Seek(1, eclipsed.Epoch);
        var renderer = scene.Renderer(0);
        if (renderer.BodyPoint(id) is not { Visible: true, MaterialOverride: ShaderMaterial material } point ||
            Math.Abs(material.GetShaderParameter("magnitude").AsSingle() + 2.5) > 1e-4 ||
            scene.Renderer(1).BodyPoint(id)?.Visible == true || scene.Renderer(1).LocalSatelliteVisible(id))
            throw new InvalidOperationException("ISS must match its archived standard magnitude at 1000 km and disappear in Earth's shadow.");
        if (renderer.PickObject(renderer.NavigationCameraNode.UnprojectPosition(point.Position)) != id)
            throw new InvalidOperationException("The sunlit ISS point must remain selectable.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the two original ISS states.");
        GD.Print("TLE photometry GPU: original ISS -2.5 at 1000 km half lit, selectable point and independent eclipse.");
    }
}
