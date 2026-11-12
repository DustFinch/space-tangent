// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseHistoricalEpoch(SolarSystemScene scene, string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")))
            return;
        var session = scene.Session!;
        if (session.World.Ephemeris is not HistoricalSolarEphemeris historical)
            throw new InvalidOperationException("The simulator did not install the historical ephemeris.");
        var epoch = new JulianInstant(2106215, 0, TimeScale.Tdb);
        var tomorrow = epoch.AddSeconds(JulianInstant.SecondsPerDay);
        ObserverView[] views = [View(epoch, PlanetCatalogue.SaturnId, 410_000), View(tomorrow, SolarSystemSample.MoonId, 7000)];
        string path = Path.Combine(directory, "historical-1054.stscene");
        ScenarioFile.Save(path, new(views, false, 0, 0, DeltaTModel.MorrisonStephenson2004));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
            session.ViewCount != 2 || session[0].Epoch != epoch || session[1].Epoch != tomorrow ||
            !scene.Renderer(0).PlanetGlobeTextured(PlanetCatalogue.SaturnId) || !scene.Renderer(1).NavigationCameraCurrent)
            throw new InvalidOperationException("The historical two-view scene did not survive the reload.");
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(directory, "historical-1054.png")) != Error.Ok)
                throw new IOException("Could not capture the historical scene.");
        }
        GD.Print("Historical scene restored: two views in 1054 with independent epochs after add-on reload.");

        ObserverView View(JulianInstant instant, ObjectId id, double range)
        {
            if (historical.SourceAt(instant) != SolarEphemerisSource.Analytic)
                throw new InvalidOperationException("The historical epoch must exercise the analytic path.");
            var world = session.World.Graph.Evaluate(instant);
            if (!world.TryState(id, out CartesianState body))
                throw new InvalidOperationException("The historical camera target is missing.");
            Vector3d outward = world.SolarSystem!.Value.Sun.PositionKilometers - body.PositionKilometers;
            outward = outward / outward.Length + new Vector3d(0, 0, .5);
            var pose = new NavigationCamera(body.PositionKilometers, range, Math.Atan2(outward.X, -outward.Y),
                Math.Asin(outward.Z / outward.Length), id, true);
            return new(instant, CameraMode.Solar, id, false) { Navigation = pose };
        }
    }
}
