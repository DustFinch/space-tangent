// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>Shared and independent observer clocks, without world or rendering dependencies.</summary>
internal sealed class ObserverClock(List<ObserverView> views)
{
    private readonly List<ObserverView> _views = views;
    private const double KeyboardMaximumRate = 1_000_000;
    private int _clockOwner;
    internal int Owner => _clockOwner;
    internal bool Synchronized { get; private set; } = true;

    // Scenario validation and view replacement belong to the session transaction.
    internal void Restore(bool synchronized, int owner)
    {
        Synchronized = synchronized;
        _clockOwner = owner;
    }

    internal void OnViewRemoved(int view)
    {
        if (_clockOwner == view)
            _clockOwner = 0;
        else if (_clockOwner > view)
            _clockOwner--;
    }

    private void SetPlaying(int view, bool playing) => SetClock(view, _views[view].Epoch, playing);

    internal void Synchronize(bool enabled, int source)
    {
        ObserverView anchor = _views[source];
        if (Synchronized == enabled)
            return;
        Synchronized = enabled;
        if (enabled)
        {
            SharePlaybackRate(anchor.PlaybackRate ?? DefaultPlaybackRate(anchor.Mode));
            SetClock(source, anchor.Epoch, anchor.Playing);
            for (int index = 0; index < _views.Count; index++)
                _views[index] = _views[index] with
                {
                    LightTimeDelayed = anchor.LightTimeDelayed,
                    LightTimeOffsetNanoseconds = anchor.LightTimeOffsetNanoseconds
                };
        }
    }

    internal void SetPlaybackRate(int view, double rate, bool synchronizeRate)
    {
        if (!ObserverView.ValidPlaybackRate(rate))
            throw new ArgumentOutOfRangeException(nameof(rate));
        double? playback = rate == 0 ? null : rate;
        _views[view] = _views[view] with { PlaybackRate = playback };
        if (synchronizeRate && Synchronized)
            for (int index = 0; index < _views.Count; index++)
                _views[index] = _views[index] with { PlaybackRate = playback };
        SetPlaying(view, rate != 0);
    }

    internal void Step(int view, double seconds, JulianInstant start, JulianInstant end)
    {
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        ObserverView previous = _views[view];
        double first = (double)start.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        double last = (double)end.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        if (seconds <= first)
            SetClock(view, start, false);
        else if (seconds >= last)
            SetClock(view, end, false);
        else
            SetClock(view, previous.Epoch.AddSeconds(seconds), previous.Playing);
    }

    internal static double DefaultPlaybackRate(CameraMode mode) => mode switch
    {
        CameraMode.Probe => 1,
        CameraMode.Satellites or CameraMode.Mission or CameraMode.Timeline => 180,
        _ => JulianInstant.SecondsPerDay
    };

    internal void SetClock(int view, JulianInstant epoch, bool playing)
    {
        if (!Synchronized)
        {
            _views[view] = _views[view] with { Epoch = epoch, Playing = playing };
            return;
        }
        double sharedRate = CurrentPlaybackRate(view);
        _clockOwner = view;
        for (int index = 0; index < _views.Count; index++)
            _views[index] = _views[index] with { Epoch = epoch, Playing = playing, PlaybackRate = sharedRate };
    }

    internal void SharePlaybackRate(double rate)
    {
        for (int index = 0; index < _views.Count; index++)
            _views[index] = _views[index] with { PlaybackRate = rate };
    }

    internal double CurrentPlaybackRate(int view)
    {
        ObserverView clock = _views[Synchronized ? _clockOwner : view];
        return clock.PlaybackRate ?? DefaultPlaybackRate(clock.Mode);
    }

    internal void AdjustPlaybackRate(int view, bool faster)
    {
        double previous = CurrentPlaybackRate(view);
        double magnitude = Math.Clamp(Math.Abs(previous) * (faster ? 10 : .1), 1, KeyboardMaximumRate);
        double next = Math.CopySign(magnitude, previous);
        for (int index = 0; index < _views.Count; index++)
            if (index == view || Synchronized)
                _views[index] = _views[index] with { PlaybackRate = next };
        SetClock(view, _views[view].Epoch, _views[view].Playing);
    }
}
