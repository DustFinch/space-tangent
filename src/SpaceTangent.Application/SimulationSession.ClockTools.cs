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
        _views[view] = _views[view] with { PlaybackRate = Math.CopySign(magnitude, previous) };
        SetClock(view, _views[view].Epoch, _views[view].Playing);
    }

    public void ReversePlayback(int view) => SetPlaybackRate(view, -CurrentPlaybackRate(view));

    public double? LightTimeSeconds(int view)
    {
        ObserverView observer = _views[view];
        if (observer.Selection is not { } id || World.Catalogue.Find(id) is not { } selected ||
            selected.Kind is CatalogueKind.Star or CatalogueKind.Galaxy or CatalogueKind.Nebula or CatalogueKind.Cluster)
            return null;
        var snapshot = World.Graph.Evaluate(observer.Epoch);
        if (snapshot.SolarSystem is not { } solar || !World.TryNavigationState(id, snapshot, out var state, DeltaT))
            return null;

        Vector3d eye;
        if (observer.Navigation is { } pose)
            eye = pose.AnchorKilometers + pose.ResolvePlacement(snapshot, DeltaT).OffsetKilometers;
        else if (observer.Mode == CameraMode.Surface)
        {
            var site = (observer.EarthObserver ?? EarthObserverCamera.Moscow).Site;
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

    /// <summary>Shift the view's clock by the light time to the selected body as measured at the current epoch.</summary>
    public bool TrySetLightTimeDelay(int view, bool enabled, out double seconds)
    {
        ObserverView observer = _views[view];
        seconds = 0;
        if (observer.LightTimeDelayed == enabled)
            return true;
        if (LightTimeSeconds(view) is not { } travel)
            return false;
        double shift = enabled ? -travel : travel;
        double first = (double)World.Ephemeris.Start.DifferenceNanoseconds(observer.Epoch) / JulianInstant.NanosecondsPerSecond;
        double last = (double)World.Ephemeris.End.DifferenceNanoseconds(observer.Epoch) / JulianInstant.NanosecondsPerSecond;
        if (shift < first || shift > last)
            return false;
        SetClock(view, observer.Epoch.AddSeconds(shift), observer.Playing);
        _views[view] = _views[view] with { LightTimeDelayed = enabled };
        seconds = travel;
        return true;
    }
}
