// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Godot.Simulation;

/// <summary>A main-thread sampling job. No incomplete array escapes, and a host frame advances at most once.</summary>
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
        if (_lastFrame == frame) return false;
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
