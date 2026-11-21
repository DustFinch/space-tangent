// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Godot;

namespace SpaceTangent.Godot.Tests;

// Godot unloads its managed context before a normal CLR ProcessExit notification.
// This opt-in test hook flushes the tracker supplied by the pinned coverage runner.
internal static class CoverageCapture
{
    // Numerical mesh construction is deliberately instrumented too; allow its bounded overhead.
    internal static int TimeoutSeconds(int ordinary) =>
        System.Environment.GetEnvironmentVariable("SPACETANGENT_COVERAGE_CAPTURE") == "1" ? checked(ordinary * 10) : ordinary;

    internal static int Complete(int exitCode, params string[] requiredModules)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_COVERAGE_CAPTURE") != "1")
            return exitCode;
        try
        {
            var known = new HashSet<string>(["SpaceTangent.Godot", "SpaceTangent.Application",
                "SpaceTangent.Core", "SpaceTangent.Contracts"], StringComparer.Ordinal);
            var expected = new HashSet<string>(requiredModules.Length == 0 ? known : requiredModules, StringComparer.Ordinal);
            if (expected.Count == 0 || !expected.IsSubsetOf(known) ||
                (requiredModules.Length != 0 && expected.Count != requiredModules.Length))
                throw new InvalidOperationException("Coverage requires a nonempty, distinct set of known modules.");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name ?? "";
                if (!known.Contains(name)) continue;
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
