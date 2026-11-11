// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

/// <summary>A view's inertial pivot, orbital camera pose, and optional tracked object.</summary>
public readonly record struct NavigationCamera(Vector3d AnchorKilometers, double RangeKilometers,
    double AzimuthRadians, double ElevationRadians, ObjectId? Target, bool Tracking)
{
    internal const double MaxElevation = 1.55;
    internal const double MinRange = .005;
    internal const double MaxRange = 12_000_000_000;

    public bool IsValid() => AnchorKilometers.IsFinite && AnchorKilometers.Length <= 1e23 &&
        RangeKilometers is >= MinRange and <= MaxRange &&
        AzimuthRadians is >= -Math.PI and <= Math.PI &&
        ElevationRadians is >= -MaxElevation and <= MaxElevation &&
        (!Tracking || Target.HasValue);

    internal NavigationCamera Adjust(double azimuth, double elevation, double zoom, double minimumRange) =>
        this with
        {
            AzimuthRadians = Math.IEEERemainder(AzimuthRadians + azimuth, 2 * Math.PI),
            ElevationRadians = Math.Clamp(ElevationRadians + elevation, -MaxElevation, MaxElevation),
            RangeKilometers = Math.Clamp(RangeKilometers * zoom, minimumRange, MaxRange)
        };

    public static double BodyRadiusKilometers(ObjectId target)
    {
        if (target == SolarSystemSample.SunId)
            return 695_700;
        if (PlanetCatalogue.Find(target) is { } planet)
            return planet.RadiusKilometers;
        if (target == SolarSystemSample.MoonId)
            return 1737.4;
        return NaturalBodyCatalogue.Find(target)?.RadiusKilometers ?? .007;
    }
}
