using System.Reflection;
using Godot;

namespace SpaceTangent.Godot.Tests;

public partial class BootstrapSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate();
            AddChild(scene);
            if (scene is not Node3D)
                throw new InvalidOperationException("The main scene must have a Node3D root.");

            Assembly.Load("SpaceTangent.Core");
            Assembly.Load("SpaceTangent.Contracts");
            GD.Print("SpaceTangent bootstrap smoke passed");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }
}
