// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Tests;

// Exercise the normal application's startup commands, including its own exit code.
// The outer runner verifies the expected code and a command-specific marker;
// this hook only flushes coverage before Godot unloads the managed context.
public partial class StartupCommandProbe : Node
{
    public override void _Ready()
    {
        AddChild(GD.Load<PackedScene>("res://Main.tscn").Instantiate());
    }

    public override void _ExitTree()
    {
        CoverageCapture.Complete(0);
        GD.Print("SpaceTangent startup command probe exited.");
    }
}
