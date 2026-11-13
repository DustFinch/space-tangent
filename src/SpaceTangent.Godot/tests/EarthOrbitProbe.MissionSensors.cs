// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionSensors(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_SENSOR_PROBE") is not ("1" or "only"))
            return;
        var session = scene.Session!;
        ObjectId jasonSensor = new("mission/jasonnativesensor"), sorceSensor = new("mission/sorcenativesensor");
        var jason = session.World.Catalogue.Search("Jason-1").First(item => item.Name == "Jason-1").Id;
        var sorce = session.World.Catalogue.Search("SORCE").First(item => item.Name == "SORCE").Id;
        string path = Path.Combine(directory, "mission-sensors");
        var deltaT = session.DeltaT;
        var first = Observer(jason, 2455755.0);
        var second = Observer(sorce, 2455755.01);
        ScenarioFile.Save(path + ".stscene", new([first, second], false, 0, 0, deltaT));
        if (!scene.LoadScenario(path + ".stscene"))
            throw new InvalidOperationException("Could not open independent native sensor views.");
        Check();
        if (!await scene.ReloadAddOns() || session.ViewCount != 2 || session.DeltaT != deltaT)
            throw new InvalidOperationException("Native sensor reload lost the observer workspace.");
        Check();
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(path + ".png") != Error.Ok)
                throw new IOException("Could not capture the native sensor mesh.");
        }

        ObserverView Observer(ObjectId id, double date)
        {
            var epoch = JulianInstant.FromJulianDate(date, TimeScale.Tdb);
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(id, out var state) || !snapshot.TryState(SolarSystemSample.EarthId, out var earth))
                throw new InvalidOperationException("The original science mission state is missing.");
            Vector3d fromEarth = (state - earth).PositionKilometers;
            Vector3d side = Vector3d.Cross(fromEarth, new(0, 0, 1));
            Vector3d eye = side / side.Length * 3200;
            var pose = new NavigationCamera(state.PositionKilometers, eye.Length, 0, 0, id, false)
                { Placement = new NavigationPlacement(eye, eye / -eye.Length, new(0, 0, 1)) };
            return new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose };
        }

        void Check()
        {
            var left = scene.Renderer(0).MissionSensorResult(jasonSensor);
            var right = scene.Renderer(1).MissionSensorResult(sorceSensor);
            if (left is not { SurfaceRays: 48, RangeClips: 0, Misses: 0, GridLines: 340, NadirError: < 1e-9 } ||
                right is not { SurfaceRays: 10, RangeClips: 8, Misses: 30, GridLines: 344, NadirError: < 1e-9 })
                throw new InvalidOperationException($"Native sensor coverage or orientation differs: Jason-1 {left}, SORCE {right}.");
            for (int view = 0; view < 2; view++)
            {
                var renderer = scene.Renderer(view);
                var faces = renderer.GetNode<MeshInstance3D>("MissionSensorFaces").Mesh;
                var grid = renderer.GetNode<MeshInstance3D>("MissionSensorGrid").Mesh;
                if (faces.GetSurfaceCount() != 1 || grid.GetSurfaceCount() != 1 ||
                    faces.SurfaceGetMaterial(0) is not StandardMaterial3D material || material.NoDepthTest ||
                    material.CullMode != BaseMaterial3D.CullModeEnum.Back ||
                    faces.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length != 288 ||
                    grid.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length != 1560)
                    throw new InvalidOperationException("The sensor triangle fan and depth-tested grid were not published.");
            }
            GD.Print($"Native sensor mesh: Jason-1 {left}; SORCE {right}.");
        }
    }
}
