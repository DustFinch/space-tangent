// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Godot.Simulation;

/// <summary>A main-thread job: one ordinary slice per frame, with an explicit synchronous completion override.</summary>
internal sealed class FrameSamples<T>(int count, Func<int, T> sample, Func<double>? milliseconds = null)
{
    private readonly T[] _values = new T[count];
    private Func<int, T>? _sample = sample;
    private readonly Func<double> _clock = milliseconds ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency);
    private long _lastFrame = long.MinValue;
    internal int Progress { get; private set; }
    internal double LastSliceMilliseconds { get; private set; }
    internal T[]? Completed => Progress == _values.Length ? _values : null;

    internal bool Advance(long frame, bool immediate = false)
    {
        if (Completed is not null) return true;
        if (_lastFrame == frame && !immediate) return false;
        _lastFrame = frame;
        double start = _clock();
        int steps = 0;
        do
        {
            _values[Progress] = _sample!(Progress);
            Progress++;
            steps++;
        } while (Progress < _values.Length && (immediate || steps < 8 && _clock() - start < 3));
        LastSliceMilliseconds = _clock() - start;
        if (Completed is not null) _sample = null;
        return Completed is not null;
    }
}

/// <summary>Keeps complete published samples while preparing a replacement; never publishes an over-age result.</summary>
internal sealed class RollingSamples<TKey, TEpoch, T> where TKey : notnull where TEpoch : struct
{
    private sealed record Work(TEpoch Epoch, FrameSamples<T> Batch);
    private TKey? _key;
    private bool _hasKey;
    private Work? _pending;
    private long _lastSamplingFrame = long.MinValue;
    internal T[]? Published { get; private set; }
    internal TEpoch? PublishedEpoch { get; private set; }
    internal bool HasPending => _pending is { Batch.Completed: null };
    internal int LastSamples { get; private set; }
    internal double LastMilliseconds { get; private set; }
    internal bool LastImmediate { get; private set; }

    internal void Reset()
    {
        _hasKey = false;
        _key = default;
        _pending = null;
        Published = null;
        PublishedEpoch = null;
    }

    internal void Continuous(TKey key, TEpoch epoch, long frame, double maximumAge,
        Func<TEpoch, TEpoch, double> distance, Func<TEpoch, FrameSamples<T>> create, bool immediate = false)
    {
        Begin(key);
        bool expiredPending = _pending is { } pending && distance(epoch, pending.Epoch) > maximumAge;
        if (expiredPending) _pending = null;
        bool expired = PublishedEpoch is { } published && distance(epoch, published) > maximumAge;
        bool force = immediate || expired || expiredPending;
        double age = PublishedEpoch is { } at ? distance(epoch, at) : double.PositiveInfinity;
        if (_pending is null && (Published is null || expired || !immediate && age > maximumAge / 2))
            _pending = new(epoch, create(epoch));
        if (_pending is { } work && Advance(work, frame, force)) Publish(work);
    }

    internal void Discrete(TKey key, TEpoch epoch, TEpoch? next, long frame,
        Func<TEpoch, FrameSamples<T>> create, bool immediate = false)
    {
        Begin(key);
        if (PublishedEpoch is not { } published || !EqualityComparer<TEpoch>.Default.Equals(published, epoch))
        {
            bool changed = Published is not null || _pending is { } old && !EqualityComparer<TEpoch>.Default.Equals(old.Epoch, epoch);
            if (_pending is not { } current || !EqualityComparer<TEpoch>.Default.Equals(current.Epoch, epoch))
                _pending = new(epoch, create(epoch));
            if (!Advance(_pending, frame, immediate || changed)) return;
            Publish(_pending);
            // Seed prefetch now, but do not spend a second slice in this frame.
            if (!immediate && next is { } after) _pending = new(after, create(after));
            return;
        }
        if (immediate || next is null)
        {
            _pending = null;
            return;
        }
        if (_pending is not { } upcoming || !EqualityComparer<TEpoch>.Default.Equals(upcoming.Epoch, next.Value))
            _pending = new(next.Value, create(next.Value));
        Advance(_pending, frame, false);
    }

    private void Begin(TKey key)
    {
        if (!_hasKey || !EqualityComparer<TKey>.Default.Equals(_key!, key))
        {
            Reset();
            _key = key;
            _hasKey = true;
        }
        LastSamples = 0;
        LastMilliseconds = 0;
        LastImmediate = false;
    }

    private bool Advance(Work work, long frame, bool immediate)
    {
        // A completed job can seed another job in the same frame; keep the ordinary allowance global to this scheduler.
        if (!immediate && _lastSamplingFrame == frame) return work.Batch.Completed is not null;
        _lastSamplingFrame = frame;
        int before = work.Batch.Progress;
        bool complete = work.Batch.Advance(frame, immediate);
        if (work.Batch.Progress != before)
        {
            LastSamples += work.Batch.Progress - before;
            LastMilliseconds += work.Batch.LastSliceMilliseconds;
            LastImmediate |= immediate;
        }
        return complete;
    }

    private void Publish(Work work)
    {
        Published = work.Batch.Completed!;
        PublishedEpoch = work.Epoch;
        _pending = null;
    }
}

/// <summary>Shares identical sampling jobs across views; a discarded world does not stay alive through this cache.</summary>
internal sealed class FrameSamplesCache<TKey, T> where TKey : notnull
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<TKey, FrameSamples<T>>> _generations = new();
    internal FrameSamples<T> Get(object generation, TKey key, Func<FrameSamples<T>> create, bool share)
    {
        if (!share) return create();
        var entries = _generations.GetOrCreateValue(generation);
        if (entries.TryGetValue(key, out var existing)) return existing;
        if (entries.Count == 16) entries.Remove(entries.Keys.First());
        return entries[key] = create();
    }
}
