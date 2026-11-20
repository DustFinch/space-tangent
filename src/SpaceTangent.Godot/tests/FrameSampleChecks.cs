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
        var escalated = new FrameSamples<int>(65, index => index, () => 0);
        if (escalated.Advance(0) || !escalated.Advance(0, immediate: true) || escalated.Progress != 65)
            throw new InvalidOperationException("A capture could not complete the remaining samples in the same frame.");
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
        CheckRolling();
        CheckDays();
        CheckInvalidation();
        CheckRepeatedRefresh();
    }

    private static void CheckRolling()
    {
        foreach (double rate in new double[] { 1, 100, 1000, 1_000_000, -1, -1000, -1_000_000 })
        {
            var rolling = new RollingSamples<int, double, (double Epoch, int Index)>();
            bool published = false;
            int forced = 0, calls = 0;
            FrameSamples<(double, int)> Create(double epoch) => new(65, index => { calls++; return (epoch, index); }, () => 0);
            for (int frame = 0; frame < 3000; frame++)
            {
                double epoch = frame * rate / 60;
                calls = 0;
                rolling.Continuous(1, epoch, frame, 5400d / 256, (a, b) => Math.Abs(a - b), Create);
                bool hadPublished = published;
                published |= rolling.Published is not null;
                if (published && (rolling.Published is not { Length: 65 } points ||
                    rolling.PublishedEpoch is not { } at || Math.Abs(at - epoch) > 5400d / 256 ||
                    points.Where((point, index) => point.Epoch != at || point.Index != index).Any()))
                    throw new InvalidOperationException($"Rolling trail disappeared, aged or mixed samples at {rate}x, frame {frame}.");
                if (calls > 65 || Math.Abs(rate) == 1 && calls > 8)
                    throw new InvalidOperationException("An update exceeded the old full-trail cost or normal slice allowance.");
                if (hadPublished && rolling.LastImmediate) forced++;
            }
            if (!published || Math.Abs(rate) == 1 && forced != 0 || Math.Abs(rate) >= 1000 && forced == 0)
                throw new InvalidOperationException("Rolling scheduling failed to prefetch or fall back at the expected rates.");
        }
        var jump = new RollingSamples<int, double, int>();
        jump.Continuous(1, 0, 0, 20, (a, b) => Math.Abs(a - b), _ => new(65, i => i, () => 0), immediate: true);
        jump.Continuous(1, 11, 1, 20, (a, b) => Math.Abs(a - b), _ => new(65, i => i, () => 0));
        jump.Continuous(1, -1000, 2, 20, (a, b) => Math.Abs(a - b), _ => new(65, i => i, () => 0));
        if (jump.PublishedEpoch != -1000 || jump.LastSamples != 65 || !jump.LastImmediate)
            throw new InvalidOperationException("A jump advanced an obsolete batch before replacing it.");
    }

    private static void CheckDays()
    {
        foreach (double rate in new double[] { 1, 100_000, 1_000_000, -100_000, -1_000_000 })
        {
            var rolling = new RollingSamples<int, double, (double Day, int Index)>();
            bool published = false;
            int calls = 0;
            FrameSamples<(double, int)> Create(double day) => new(121, index => { calls++; return (day, index); }, () => 0);
            for (int frame = 0; frame < 3000; frame++)
            {
                double today = Math.Floor(frame * rate / 60 / 86400);
                calls = 0;
                rolling.Discrete(1, today, today + Math.Sign(rate), frame, Create);
                published |= rolling.Published is not null;
                if (published && (rolling.PublishedEpoch != today || rolling.Published is not { Length: 121 } points ||
                    points.Where((point, index) => point.Day != today || point.Index != index).Any()))
                    throw new InvalidOperationException($"The body path disappeared or used another day at {rate}x, frame {frame}.");
                if (calls > 121 || !rolling.LastImmediate && calls > 8)
                    throw new InvalidOperationException("A daily update exceeded its full-path or slice allowance.");
                if (Math.Abs(rate) == 100_000 && frame > 16 && rolling.LastImmediate)
                    throw new InvalidOperationException("The next or previous day should finish before this boundary.");
            }
            if (!published) throw new InvalidOperationException("A fast daily path never published.");
        }
        var bounded = new RollingSamples<int, double, int>();
        bounded.Discrete(1, 0, null, 0, _ => new(121, i => i), immediate: true);
        bounded.Discrete(1, 0, null, 1, _ => throw new InvalidOperationException("A boundary prefetched an invalid day."));
        if (bounded.HasPending || bounded.PublishedEpoch != 0)
            throw new InvalidOperationException("A calendar boundary lost its published path.");
    }

    private static void CheckInvalidation()
    {
        var original = (World: 1, Target: 2, Center: 3, Style: 4, Model: 5);
        var keys = new[] { (9, 2, 3, 4, 5), (1, 9, 3, 4, 5), (1, 2, 9, 4, 5), (1, 2, 3, 9, 5), (1, 2, 3, 4, 9) };
        foreach (var key in keys)
        {
            var rolling = new RollingSamples<(int, int, int, int, int), double, int>();
            FrameSamples<int> Create(double _) => new(65, index => index, () => 0);
            rolling.Continuous(original, 0, 0, 20, (a, b) => Math.Abs(a - b), Create, immediate: true);
            rolling.Continuous(key, 0, 1, 20, (a, b) => Math.Abs(a - b), Create);
            if (rolling.Published is not null || !rolling.HasPending)
                throw new InvalidOperationException("A changed target, center, style, Delta T or world retained another path.");
            rolling.Continuous(key, 0, 1, 20, (a, b) => Math.Abs(a - b), Create, immediate: true);
            if (rolling.Published is not { Length: 65 } || rolling.HasPending)
                throw new InvalidOperationException("Same-frame capture completion failed through the rolling scheduler.");
            rolling.Reset();
            if (rolling.Published is not null || rolling.PublishedEpoch is not null || rolling.HasPending)
                throw new InvalidOperationException("Hidden paths retained a stale publication.");
        }
    }

    private static void CheckRepeatedRefresh()
    {
        int calls = 0;
        var days = new RollingSamples<int, double, int>();
        FrameSamples<int> Create(double _) => new(9, index => { calls++; return index; }, () => 0);
        days.Discrete(1, 0, 1, 0, Create);
        calls = 0;
        days.Discrete(1, 0, 1, 1, Create); // Finishes the first day and seeds the next.
        days.Discrete(1, 0, 1, 1, Create);
        if (calls != 1 || days.PublishedEpoch != 0 || !days.HasPending)
            throw new InvalidOperationException("A second refresh spent another ordinary slice after publication.");
        calls = 0;
        days.Discrete(1, 0, 1, 2, Create);
        days.Discrete(1, 0, 1, 2, Create);
        if (calls != 8) throw new InvalidOperationException("Repeated refreshes exceeded the shared ordinary allowance.");
        days.Discrete(1, 1, 2, 2, Create); // Boundary override still finishes the remaining point in this frame.
        if (calls != 9 || days.PublishedEpoch != 1 || !days.LastImmediate)
            throw new InvalidOperationException("The per-frame guard prevented a necessary day-boundary completion.");
    }
}
