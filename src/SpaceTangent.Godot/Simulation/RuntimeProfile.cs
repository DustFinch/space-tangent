// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Diagnostics;

namespace SpaceTangent.Godot.Simulation;

/// <summary>Opt-in bounded timing samples for reproducible startup and frame probes.</summary>
internal static class RuntimeProfile
{
    private static readonly bool Enabled = System.Environment.GetEnvironmentVariable("SPACETANGENT_PROFILE") == "1";
    private static readonly Dictionary<string, List<double>> Samples = [];
    internal static Scope Measure(string name) => new(Enabled ? name : null);
    internal static T Run<T>(string name, Func<T> action) { using var scope = Measure(name); return action(); }
    internal static async Task<T> RunAsync<T>(string name, Func<Task<T>> action)
    { using var scope = Measure(name); return await action(); }

    internal static void Record(string name, double milliseconds)
    {
        if (!Enabled) return;
        lock (Samples)
        {
            if (!Samples.TryGetValue(name, out var samples)) Samples[name] = samples = [];
            if (samples.Count == 4096) samples.RemoveAt(0);
            samples.Add(milliseconds);
        }
    }

    internal static Dictionary<string, object> Snapshot()
    {
        lock (Samples)
            return Samples.ToDictionary(pair => pair.Key, pair =>
            {
                double[] sorted = pair.Value.Order().ToArray();
                return (object)new { Count = sorted.Length, Median = sorted[sorted.Length / 2],
                    P95 = sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * .95) - 1)], Max = sorted[^1] };
            });
    }

    internal readonly struct Scope(string? name) : IDisposable
    {
        private readonly long _start = name is null ? 0 : Stopwatch.GetTimestamp();
        public void Dispose() { if (name is not null) Record(name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds); }
    }
}
