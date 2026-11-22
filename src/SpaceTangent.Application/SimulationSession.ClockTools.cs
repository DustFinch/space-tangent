// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    public const double LightKilometersPerSecond = 299_792.458;
    private const double KeyboardMaximumRate = 1_000_000;

    public double CurrentPlaybackRate(int view)
    {
        ObserverView clock = _views[Synchronized ? _clockOwner : view];
        return clock.PlaybackRate ?? DefaultPlaybackRate(clock.Mode);
    }

    public void AdjustPlaybackRate(int view, bool faster)
    {
        double previous = CurrentPlaybackRate(view);
        double magnitude = Math.Clamp(Math.Abs(previous) * (faster ? 10 : .1), 1, KeyboardMaximumRate);
        double next = Math.CopySign(magnitude, previous);
        for (int index = 0; index < _views.Count; index++)
            if (index == view || Synchronized)
                _views[index] = _views[index] with { PlaybackRate = next };
        SetClock(view, _views[view].Epoch, _views[view].Playing);
    }

    public void ReversePlayback(int view) => SetPlaybackRate(view, -CurrentPlaybackRate(view));

    public double? LightTimeSeconds(int view)
    {
        ObserverView observer = _views[view];
        if (observer.Selection is not { } id || World.Catalogue.Find(id) is not { } selected ||
            selected.Kind is CatalogueKind.Star or CatalogueKind.Galaxy or CatalogueKind.Nebula or CatalogueKind.Cluster)
            return null;
        var snapshot = World.Graph.Evaluate(observer.Epoch, DeltaT);
        if (snapshot.SolarSystem is not { } solar || !World.TryNavigationState(id, snapshot, out var state, DeltaT))
            return null;

        Vector3d eye;
        if (observer.Navigation is { } pose)
            eye = pose.AnchorKilometers + pose.ResolvePlacement(snapshot, DeltaT).OffsetKilometers;
        else if (observer.Mode == CameraMode.Surface)
        {
            var site = (observer.EarthObserver ?? EarthObserverCamera.Default).Site;
            try { eye = solar.Earth.PositionKilometers + EarthSurfaceLighting.Sample(solar, site, DeltaT).PositionKilometers; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        else
        {
            pose = PresetCamera(observer, snapshot);
            eye = pose.AnchorKilometers + pose.ResolvePlacement(snapshot, DeltaT).OffsetKilometers;
        }
        double seconds = (state.PositionKilometers - eye).Length / LightKilometersPerSecond;
        return double.IsFinite(seconds) ? seconds : null;
    }

    /// <summary>
    /// Shifts the view's clock back by the light time to the selected body, measured at the current epoch. Turning the
    /// delay off undoes the shift that was applied, even when the selection or the distance has changed since. In
    /// synchronized views the shift moves the shared clock, so every view is marked.
    /// </summary>
    public bool TrySetLightTimeDelay(int view, bool enabled, out double seconds)
    {
        ObserverView observer = _views[view];
        seconds = 0;
        if (observer.LightTimeDelayed == enabled)
            return true;
        double travel;
        if (!enabled && observer.LightTimeOffsetNanoseconds is { } applied)
            travel = (double)applied / JulianInstant.NanosecondsPerSecond;
        else if (LightTimeSeconds(view) is { } measured)
            travel = measured;
        else
            return false;
        double nanoseconds = Math.Round(travel * JulianInstant.NanosecondsPerSecond, MidpointRounding.ToEven);
        if (!double.IsFinite(nanoseconds) || nanoseconds < 0 || nanoseconds >= long.MaxValue)
            return false;
        long offset = !enabled && observer.LightTimeOffsetNanoseconds is { } saved ? saved : (long)nanoseconds;
        Int128 shift = enabled ? -(Int128)offset : offset;
        if (shift < World.Ephemeris.Start.DifferenceNanoseconds(observer.Epoch) ||
            shift > World.Ephemeris.End.DifferenceNanoseconds(observer.Epoch))
            return false;
        SetClock(view, observer.Epoch.AddNanoseconds(shift), observer.Playing);
        for (int index = 0; index < _views.Count; index++)
        {
            if (index != view && !Synchronized)
                continue;
            _views[index] = _views[index] with
            {
                LightTimeDelayed = enabled,
                LightTimeOffsetNanoseconds = enabled ? offset : null
            };
        }
        seconds = travel;
        return true;
    }

}
