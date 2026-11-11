// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class BootstrapSmoke : Node
{
    public override async void _Ready()
    {
        try
        {
            Assembly.Load("SpaceTangent.Core");
            Assembly.Load("SpaceTangent.Contracts");
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            var startup = System.Diagnostics.Stopwatch.StartNew();
            while (!scene.InitializationFinished)
            {
                Require(startup.Elapsed.TotalSeconds < 20, "World initialization timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                CheckSimulation(scene);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                await CheckSatellitesAndReload(scene);

            GD.Print("SpaceTangent bootstrap smoke passed");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    private static void CheckSimulation(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session ?? throw new InvalidOperationException("DE440 failed to load.");
        var initial = session[0].Epoch;
        Require(initial == new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb), "The clock must start at J2000.");

        var renderer = scene.Renderer(0);
        Vector3 initialPosition = renderer.EarthPosition;
        Require(initialPosition.Length() is >= 140 and <= 160, "The solar view must show Earth about one AU from the Sun.");
        scene.Advance(0, JulianInstant.SecondsPerDay);
        Require(session[0].Epoch == initial.AddSeconds(JulianInstant.SecondsPerDay) &&
                renderer.EarthPosition.DistanceTo(initialPosition) >= 1, "Advancing the clock must move Earth.");

        scene.SetMode(0, CameraMode.Lunar);
        Require(renderer.MoonVisible && renderer.EarthPosition == Vector3.Zero &&
                renderer.MoonPosition.Length() is >= 85 and <= 105, "The lunar view must show the Moon around Earth.");
        GD.Print("SpaceTangent DE440 scene smoke passed");
    }

    private static async Task CheckSatellitesAndReload(SolarSystemScene scene)
    {
        var jasonId = new ObjectId("norad/26997");
        var sorceId = new ObjectId("norad/27651");
        SimulationSession session = scene.Session ?? throw new InvalidOperationException("DE440 failed to load.");
        scene.SetMode(0, CameraMode.Satellites);
        scene.SelectSatellite(0, jasonId);
        VerifyMesh(scene.Renderer(0).SatelliteMesh(jasonId), 83_233, 28, 2);
        VerifyMesh(scene.Renderer(0).SatelliteMesh(sorceId), 115_711, 20, 1);

        scene.ToggleSplit();
        Require(session.ViewCount == 2 && session[0] == session[1], "Splitting must copy view state.");
        scene.Synchronize(false, 0);
        scene.Advance(1, 3600);
        Require(session[0].Epoch != session[1].Epoch, "Independent clocks must diverge.");
        Require(scene.Renderer(1).SatelliteMesh(jasonId).Position
                    .DistanceTo(scene.Renderer(0).SatelliteMesh(jasonId).Position) > 5, "Two epochs must move Jason-1 independently.");

        string? oldSettings = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH");
        string settings = Path.Combine(Path.GetTempPath(), $"SpaceTangent-smoke-{Guid.NewGuid():N}.txt");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", settings);
        try
        {
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            ObserverView left = session[0], right = session[1];
            Require(await scene.ReloadAddOns(), "Real add-ons must reload.");
            Require(left == session[0] && right == session[1] && session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "Reload must retain both clocks, selections, camera modes and ΔT.");
            Require(File.ReadAllText(settings) == nameof(DeltaTModel.MorrisonStephenson2004), "ΔT must be saved to the profile.");
            VerifyMesh(scene.Renderer(1).SatelliteMesh(jasonId), 83_233, 28, 2);
            await CheckFailedReload(scene, left, right);
            Task<bool> superseded = scene.ReloadAddOns();
            Task<bool> latest = scene.ReloadAddOns();
            bool firstPublished = await superseded;
            bool latestPublished = await latest;
            Require(!firstPublished && latestPublished && left == session[0] && right == session[1],
                "A superseded load must not replace the latest world generation.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", oldSettings);
            File.Delete(settings);
        }

        var beforeReload = session[1].Epoch;
        session.TogglePlaying(1);
        bool loadedWhilePlaying = await scene.ReloadAddOns();
        session.TogglePlaying(1);
        Require(loadedWhilePlaying && session[1].Epoch.CompareTo(beforeReload) > 0,
            "The second view's clock must keep advancing while add-on models decode.");

        scene.Synchronize(true, 1);
        Require(session[0].Epoch == session[1].Epoch, "Synchronizing must use the chosen view's epoch.");
        JulianInstant synchronized = session[0].Epoch;
        scene.Advance(0, 3600);
        Require(session[0].Epoch == synchronized.AddSeconds(3600) && session[1].Epoch == session[0].Epoch,
            "A synchronized step must move both clocks.");

        GD.Print("SpaceTangent Jason-1 and SORCE two-view reload smoke passed");
    }

    private static async Task CheckFailedReload(SolarSystemScene scene, ObserverView left, ObserverView right)
    {
        string? original = System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        SimulationSession session = scene.Session!;
        SimulationWorld world = session.World;
        try
        {
            bool published = await scene.ReloadAddOns();
            Require(!published && ReferenceEquals(world, session.World) && left == session[0] && right == session[1],
                "A failed reload must leave the published world and views intact.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", original);
        }
    }

    private static void VerifyMesh(MeshInstance3D node, int triangles, int surfaces, int textureCount)
    {
        if (!node.Visible || node.Mesh is not ArrayMesh mesh || node.Position.Length() is < 85 or > 120)
            throw new InvalidOperationException("Expected a real mesh in Earth orbit.");
        Require(mesh.GetSurfaceCount() == surfaces, "Unexpected number of material surfaces.");

        int vertices = 0;
        var textures = new HashSet<ulong>();
        for (int i = 0; i < surfaces; i++)
        {
            vertices += mesh.SurfaceGetArrayLen(i);
            if (mesh.SurfaceGetMaterial(i) is not StandardMaterial3D material ||
                material.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                throw new InvalidOperationException("Unexpected transparent material.");
            if (material.AlbedoTexture is { } texture)
                textures.Add(texture.GetInstanceId());
        }
        Require(vertices == triangles * 3 && textures.Count == textureCount, "Unexpected geometry or texture count.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
