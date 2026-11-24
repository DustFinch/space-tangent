// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

// Engine-local test protocol: no system input, implicit retries or swallowed
// failures. Scenarios keep their own setup, cleanup, markers and exit decisions.
internal static class ProbeHost
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Until(Node owner, Func<bool> complete, double seconds, string timeoutMessage)
    {
        var clock = Stopwatch.StartNew();
        while (!complete())
        {
            Require(clock.Elapsed.TotalSeconds < seconds, timeoutMessage);
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    internal static void FreeScene(Node owner, SolarSystemScene? scene)
    {
        if (scene is null || !GodotObject.IsInstanceValid(scene)) return;
        if (scene.GetParent() == owner) owner.RemoveChild(scene);
        scene.Free();
    }

    internal static void Complete(Node owner, int exitCode) =>
        owner.GetTree().Quit(CoverageCapture.Complete(exitCode));
}
