// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

/// <summary>Per-view site, direction and horizon overlays for the Earth surface camera.</summary>
public readonly record struct EarthObserverCamera(EarthSite Site, double AzimuthDegrees, double ElevationDegrees,
    bool HorizonGrid = false, bool CardinalPoints = false)
{
    public static EarthObserverCamera Moscow { get; } = new(new(55.7558, 37.6173, .15), 237, 28);
    public static EarthObserverCamera Tromso { get; } = new(new(69.6492, 18.9553, .01), 180, 10);

    public bool IsValid() => Site.IsValid() && Site.HeightKilometers <= 100 &&
        AzimuthDegrees is >= 0 and < 360 && ElevationDegrees is >= -90 and <= 90;
}
