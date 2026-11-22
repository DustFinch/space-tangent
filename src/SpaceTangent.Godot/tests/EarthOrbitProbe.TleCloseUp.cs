// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseTleCloseUp(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var epoch = session.World.LocalSatellites.First().Epoch;
        var snapshot = session.World.Graph.Evaluate(epoch);
        var solar = snapshot.SolarSystem ?? throw new InvalidOperationException("The close-up needs solar illumination.");
        var satellite = session.World.LocalSatellites.First(candidate =>
        {
            if (!snapshot.TryState(candidate.Id, out var state)) return false;
            var sunward = solar.Sun.PositionKilometers - state.PositionKilometers;
            return Vector3d.Dot(state.PositionKilometers - solar.Earth.PositionKilometers, sunward) > 0;
        });
        snapshot.TryState(satellite.Id, out var target);
        var outward = solar.Sun.PositionKilometers - target.PositionKilometers; outward /= outward.Length;
        var up = Vector3d.Cross(Vector3d.Cross(outward, new(0, 0, 1)), outward); up /= up.Length;
        var sky = new SkyLayerSettings(false, true, false, false) { HideSatellites = true, Starlink = false };
        string path = Path.Combine(directory, "tle-close-up.stscene");
        var near = View(.02, satellite.Id);
        var far = View(100, satellite.Id);
        ScenarioFile.Save(path, new([near, far], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path)) throw new InvalidOperationException("The real TLE close-up must open.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        scene.Seek(0, epoch); scene.Seek(1, epoch);
        var left = scene.Renderer(0); var right = scene.Renderer(1);
        if (!left.LocalSatelliteVisible(satellite.Id) || right.LocalSatelliteVisible(satellite.Id) || right.BodyPoint(satellite.Id)?.Visible != true)
            throw new InvalidOperationException($"The selected satellite must resolve nearby and retain a magnitude-based point at 100 km: near={left.LocalSatelliteVisible(satellite.Id)}, far={right.LocalSatelliteVisible(satellite.Id)}, point={right.BodyPoint(satellite.Id)?.Visible}, id={satellite.Id}.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = left.GetViewport().GetTexture().GetImage();
        if (image.IsEmpty()) throw new InvalidOperationException("The real TLE close-up must produce a rendered image.");
        var trackedOnly = near with { Selection = null };
        ScenarioFile.Save(path, new([trackedOnly, far], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !left.LocalSatelliteVisible(satellite.Id))
            throw new InvalidOperationException("A tracked satellite must remain resolved while the general satellite layer is hidden.");
        var hidden = trackedOnly with { Navigation = trackedOnly.Navigation!.Value with { Target = null } };
        ScenarioFile.Save(path, new([hidden, far], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || left.LocalSatelliteVisible(satellite.Id) || left.BodyPoint(satellite.Id)?.Visible == true)
            throw new InvalidOperationException("Clearing both the selection and tracked target must hide the disabled satellite layer.");
        if (!await scene.ReloadAddOns() || left.LocalSatelliteVisible(satellite.Id) || right.BodyPoint(satellite.Id)?.Visible != true)
            throw new InvalidOperationException("Reload must preserve independent close-up and photometric visibility.");
        GD.Print($"TLE close-up GPU: real {satellite.Name}, resolved at 20 m, magnitude point at 100 km, tracked-only retention and independent reload passed.");

        ObserverView View(double range, ObjectId? selected)
        {
            var pose = new NavigationCamera(target.PositionKilometers, range, 0, 0, satellite.Id, false)
                { Placement = new(outward * range, outward * -1, up) };
            return new(epoch, CameraMode.Solar, selected, false) { Navigation = pose, Sky = sky };
        }
    }
}
