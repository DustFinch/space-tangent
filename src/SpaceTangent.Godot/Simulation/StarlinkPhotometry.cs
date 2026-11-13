// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal static class StarlinkPhotometry
{
    internal static bool IsMember(LocalTleSatellite satellite) =>
        satellite.Name.StartsWith("STARLINK-", StringComparison.OrdinalIgnoreCase) ||
        satellite.Aliases.Contains("starlink", StringComparer.OrdinalIgnoreCase);

    internal static double? Magnitude(Vector3d place, Vector3d eye, Vector3d sun, double standard)
    {
        double along = Vector3d.Dot(place, sun);
        if (along < 0 && (place - sun * along).Length < EarthSurfaceLighting.EarthRadiusKilometers)
            return null;
        Vector3d towardEye = eye - place;
        double distance = towardEye.Length;
        if (!(distance > 0))
            return null;
        double fraction = Math.Max(1e-6, (1 + Vector3d.Dot(towardEye / distance, sun)) / 2);
        return standard + 5 * Math.Log10(distance / 1000) - 2.5 * Math.Log10(2 * fraction);
    }
}
