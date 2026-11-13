// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionObservations(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_OBSERVATION_PROBE") is not ("1" or "only"))
            return;
        var session = scene.Session!;
        ObjectId map = new("mission/jason-footprints"), clipped = new("mission/sorce-footprints");
        var earth = SolarSystemSample.EarthId;
        var jason = new ObjectId("norad/26997");
        if (session.World.Catalogue.Find(map) is null || session.World.Catalogue.Find(clipped) is null)
            throw new InvalidOperationException("The authored schedules for the original science tracks are missing.");

        string path = Path.Combine(directory, "mission-footprints");
        ScenarioFile.Save(path + ".stscene", new([Observer(2455754.999), Observer(2455755.001)], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path + ".stscene"))
            throw new InvalidOperationException("Could not open the two independent footprint epochs.");
        Check();
        var before = session.CaptureScenario(0);
        if (!await scene.ReloadAddOns() || !session.CaptureScenario(0).Views.SequenceEqual(before.Views) ||
            session.DeltaT != before.DeltaT)
            throw new InvalidOperationException("Footprints could not reload.");
        Check();
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(path + ".png") != Error.Ok)
                throw new IOException("Could not capture the footprint overlays.");
        }

        ObserverView Observer(double date)
        {
            var epoch = JulianInstant.FromJulianDate(date, TimeScale.Tdb);
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(earth, out var target) || !snapshot.TryState(jason, out var craft))
                throw new InvalidOperationException("The original Jason-1 and Earth positions are required.");
            Vector3d radial = (craft - target).PositionKilometers;
            Vector3d zenith = radial / radial.Length;
            Vector3d up = new Vector3d(0, 0, 1) - zenith * zenith.Z;
            var pose = new NavigationCamera(target.PositionKilometers, 10600, 0, 0, earth, false)
                { Placement = new NavigationPlacement(zenith * 10600, zenith * -1, up / up.Length) };
            return new(epoch, CameraMode.Solar, earth, false) { Navigation = pose };
        }

        void Check()
        {
            var early = scene.Renderer(0);
            var late = scene.Renderer(1);
            var mesh = late.ImportedBodyNode(map)?.Mesh;
            if (early.ObservationFootprintCount(map) != 0 || late.ObservationFootprintCount(map) != 7 ||
                late.ObservationFootprintCount(clipped) != 0 || mesh?.GetSurfaceCount() != 2 ||
                mesh.SurfaceGetMaterial(0) is not StandardMaterial3D { NoDepthTest: false })
                throw new InvalidOperationException("The footprint schedule, range rejection or depth-tested surface is wrong.");
            GD.Print("Mission footprints: 3 discrete and 4 swath samples, partial SORCE field rejected, independent view epochs.");
        }
    }
}
