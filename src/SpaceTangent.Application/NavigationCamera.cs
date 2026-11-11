// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>A view's inertial pivot, orbital camera pose, and optional tracked object.</summary>
public readonly record struct NavigationCamera(Vector3d AnchorKilometers, double RangeKilometers,
    double AzimuthRadians, double ElevationRadians, ObjectId? Target, bool Tracking)
{
    public NavigationPlacement? Placement { get; init; }

    internal const double MaxElevation = 1.55;
    internal const double MinRange = .005;
    internal const double MaxRange = 5e21;

    public bool IsValid() => AnchorKilometers.IsFinite && AnchorKilometers.Length <= 1e23 &&
        RangeKilometers is >= MinRange and <= MaxRange &&
        AzimuthRadians is >= -Math.PI and <= Math.PI &&
        ElevationRadians is >= -MaxElevation and <= MaxElevation &&
        (!Tracking || Target.HasValue) &&
        (Placement is not { } placement || (placement.IsValid(RangeKilometers) && (!placement.BodyFixed || Tracking)));

    public NavigationPlacement ResolvePlacement(WorldSnapshot snapshot)
    {
        if (Placement is { BodyFixed: false } inertial)
            return inertial;
        if (Placement is { BodyFixed: true } local && Target is { } target &&
            snapshot.TryOrientation(target, out FrameKinematics rotation))
            return local.Transform(rotation.ToWorld, false);
        var (sinA, cosA) = Math.SinCos(AzimuthRadians);
        var (sinE, cosE) = Math.SinCos(ElevationRadians);
        Vector3d outward = new(cosE * sinA, -cosE * cosA, sinE);
        Vector3d forward = outward * -1;
        Vector3d right = Vector3d.Cross(forward, new(0, 0, 1));
        Vector3d up = Vector3d.Cross(right / right.Length, forward);
        return new(outward * RangeKilometers, forward, up);
    }

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
