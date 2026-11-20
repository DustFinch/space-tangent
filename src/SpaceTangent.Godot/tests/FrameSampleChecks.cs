// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

internal static class FrameSampleChecks
{
    internal static void Run()
    {
        double clock = 0;
        var reads = new List<int>();
        var batch = new FrameSamples<int>(5, index => { reads.Add(index); clock += 2; return index * 7; }, () => clock);
        if (batch.Advance(0) || batch.Progress != 2 || batch.Completed is not null || batch.Advance(0) || batch.Progress != 2)
            throw new InvalidOperationException("A frame exceeded its sampling budget or exposed partial results.");
        if (batch.Advance(1) || !batch.Advance(2) || !batch.Completed!.SequenceEqual(new[] { 0, 7, 14, 21, 28 }) ||
            !reads.SequenceEqual(new[] { 0, 1, 2, 3, 4 }))
            throw new InvalidOperationException("Sampling reordered or duplicated points.");
        var immediate = new FrameSamples<int>(20, index => index);
        if (!immediate.Advance(0, immediate: true)) throw new InvalidOperationException("Synchronous compatibility sampling failed.");
        var cache = new FrameSamplesCache<(int Target, int Epoch, int Model), int>();
        object world = new();
        FrameSamples<int> Create() => new(2, index => index);
        var shared = cache.Get(world, (1, 2, 3), Create, true);
        if (!ReferenceEquals(shared, cache.Get(world, (1, 2, 3), Create, true)) ||
            ReferenceEquals(shared, cache.Get(world, (1, 2, 4), Create, true)) ||
            ReferenceEquals(shared, cache.Get(world, (1, 3, 3), Create, true)) ||
            ReferenceEquals(shared, cache.Get(world, (2, 2, 3), Create, true)) ||
            ReferenceEquals(shared, cache.Get(new object(), (1, 2, 3), Create, true)) ||
            ReferenceEquals(shared, cache.Get(world, (1, 2, 3), Create, false)))
            throw new InvalidOperationException("Sampling mixed a target, epoch, model or world generation.");
    }
}
