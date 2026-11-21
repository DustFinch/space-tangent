// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Godot;

namespace SpaceTangent.Godot.Tests;

// Godot unloads its managed context before a normal CLR ProcessExit notification.
// This opt-in test hook flushes the tracker supplied by the pinned coverage runner.
internal static class CoverageCapture
{
    internal static int Complete(int exitCode)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_COVERAGE_CAPTURE") != "1")
            return exitCode;
        try
        {
            var expected = new HashSet<string>(["SpaceTangent.Godot", "SpaceTangent.Application",
                "SpaceTangent.Core", "SpaceTangent.Contracts"], StringComparer.Ordinal);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name ?? "";
                if (!expected.Contains(name)) continue;
                var tracker = assembly.GetTypes().SingleOrDefault(type =>
                    type.Namespace?.StartsWith("Coverlet.Core.Instrumentation", StringComparison.Ordinal) == true &&
                    type.GetField("HitsArray", BindingFlags.Public | BindingFlags.Static) is not null &&
                    type.GetMethod("UnloadModule", BindingFlags.Public | BindingFlags.Static) is not null);
                if (tracker is null)
                    throw new InvalidOperationException($"Coverage tracker missing for {name} at {assembly.Location}.");
                tracker.GetMethod("UnloadModule", BindingFlags.Public | BindingFlags.Static)!
                    .Invoke(null, [null, EventArgs.Empty]);
                expected.Remove(name);
                GD.Print($"SpaceTangent coverage flushed: {name}");
            }
            if (expected.Count != 0)
                throw new InvalidOperationException("Coverage modules not loaded: " + string.Join(", ", expected));
            return exitCode;
        }
        catch (Exception error)
        {
            GD.PushError("Coverage capture failed: " + error);
            return 1;
        }
    }
}
