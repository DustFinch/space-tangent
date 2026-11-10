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
}
