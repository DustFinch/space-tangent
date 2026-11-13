// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseLegacySurfaceBody(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var epoch = new JulianInstant(2460600, 0, TimeScale.Tdb);
        var earth = new ObserverView(epoch, CameraMode.Surface, null, false)
        {
            EarthObserver = EarthObserverCamera.Moscow with { AzimuthDegrees = 90, ElevationDegrees = 15 }
        };
        var second = earth with { EarthObserver = EarthObserverCamera.Tromso };
        var model = session.DeltaT;
        string path = Path.Combine(directory, "mars-observer.stscene");
        ScenarioFile.Save(path, new([earth, second], false, 0, 0, model));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the two surface observers.");
        string profile = Path.Combine(directory, "archived-mars-observer.json");
        File.WriteAllText(profile, """
            {"SiteBody":"Sol/Mars","Site":{"LatitudeDegrees":0,"LongitudeDegrees":0,"AltitudeMeters":10000}}
            """);
        await scene.ImportStartupSettings(profile);
        var imported = session[0];
        var pose = imported.Navigation ?? throw new InvalidOperationException("The Mars surface camera was not created.");
        var placement = pose.Placement ?? throw new InvalidOperationException("The Mars observer has no local pose.");
        if (!pose.Tracking || pose.Target != PlanetCatalogue.MarsId || !placement.BodyFixed ||
            Math.Abs(placement.OffsetKilometers.X - (PlanetCatalogue.Find(PlanetCatalogue.MarsId)!.RadiusKilometers + 10.003)) > 1e-5 ||
            (placement.Forward - new Vector3d(Math.Sin(Math.PI / 12), Math.Cos(Math.PI / 12), 0)).Length > 1e-8 ||
            session[1] != second)
            throw new InvalidOperationException("The imported Mars coordinates, altitude or pointing are incorrect.");

        var before = session.World.Graph.Evaluate(epoch);
        scene.Advance(0, 3600);
        var after = session.World.Graph.Evaluate(session[0].Epoch);
        var moved = session[0].Navigation!.Value;
        if (moved.Placement != placement || moved.AnchorKilometers == pose.AnchorKilometers ||
            Vector3d.Dot(moved.ResolvePlacement(after, model).Forward, pose.ResolvePlacement(before, model).Forward) > .999)
            throw new InvalidOperationException("The observer must rotate with Mars without changing its local pose.");
        var expected = session.CaptureScenario(0);
        ScenarioFile.Save(path, expected);
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            !session.CaptureScenario(0).Views.SequenceEqual(expected.Views) || session.DeltaT != model)
            throw new InvalidOperationException("The Mars surface pose, other view and ΔT must survive reload.");

        for (int frame = 0; frame < 4; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the restored Mars observer.");
        GD.Print("Legacy surface observer GPU: Mars at 0°, 0°, 10 km, correct eastward bearing and fixed frame after time step and reload.");
    }
}
