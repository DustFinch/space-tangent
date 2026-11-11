// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class BootstrapSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            Assembly.Load("SpaceTangent.Core");
            Assembly.Load("SpaceTangent.Contracts");
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                CheckSimulation(scene);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                CheckSatellites(scene);

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
        var initial = scene.CurrentSample ?? throw new InvalidOperationException("DE440 failed to load.");
        if (initial.Epoch != new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb))
            throw new InvalidOperationException("The simulation must start at J2000.");

        var earth = scene.GetNode<Node3D>("Earth");
        Vector3 initialPosition = earth.Position;
        if (initialPosition.Length() is < 140 or > 160)
            throw new InvalidOperationException("The overview must show Earth at about one AU from the Sun.");

        scene.Advance(JulianInstant.SecondsPerDay);
        var advanced = scene.CurrentSample ?? throw new InvalidOperationException("Simulation stopped unexpectedly.");
        if (advanced.Epoch != initial.Epoch.AddSeconds(JulianInstant.SecondsPerDay) ||
            earth.Position.DistanceTo(initialPosition) < 1)
            throw new InvalidOperationException("Advancing the clock must move Earth.");

        scene.SetLunarView(true);
        var moon = scene.GetNode<Node3D>("Moon");
        if (!moon.Visible || earth.Position != Vector3.Zero || moon.Position.Length() is < 85 or > 105)
            throw new InvalidOperationException("The lunar view must show the Moon around Earth.");

        GD.Print("SpaceTangent DE440 scene smoke passed");
    }

    private static void CheckSatellites(SolarSystemScene scene)
    {
        scene.SetSatelliteView();
        VerifyMesh(scene.GetNode<MeshInstance3D>("Jason-1"), 83_233, 28, 2);
        VerifyMesh(scene.GetNode<MeshInstance3D>("SORCE"), 115_711, 20, 1);
        var jason = scene.GetNode<MeshInstance3D>("Jason-1");
        Vector3 before = jason.Position;
        scene.Advance(600);
        if (jason.Position.DistanceTo(before) < 5)
            throw new InvalidOperationException("Advancing time must change Jason-1's scene position.");

        GD.Print("SpaceTangent Jason-1 and SORCE scene smoke passed");
    }

    private static void VerifyMesh(MeshInstance3D node, int triangles, int surfaces, int textureCount)
    {
        if (!node.Visible || node.Mesh is not ArrayMesh mesh || node.Position.Length() is < 85 or > 120)
            throw new InvalidOperationException($"{node.Name} must show a real mesh in Earth orbit.");
        if (mesh.GetSurfaceCount() != surfaces)
            throw new InvalidOperationException($"{node.Name} has the wrong number of material surfaces.");

        int vertices = 0;
        var textures = new HashSet<ulong>();
        for (int i = 0; i < surfaces; i++)
        {
            vertices += mesh.SurfaceGetArrayLen(i);
            if (mesh.SurfaceGetMaterial(i) is not StandardMaterial3D material ||
                material.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                throw new InvalidOperationException($"{node.Name} contains an unexpected transparent material.");
            if (material.AlbedoTexture is { } texture)
                textures.Add(texture.GetInstanceId());
        }
        if (vertices != triangles * 3 || textures.Count != textureCount)
            throw new InvalidOperationException($"{node.Name} has unexpected geometry or texture counts.");
    }
}
