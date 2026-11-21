// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

internal static class ObserverPhotometry
{
    internal static double? Estimate(SimulationWorld world, ObjectId target, CartesianState body, Vector3d eye, CartesianState sun)
    {
        const double au = 149_597_870.7;
        var offset = body.PositionKilometers - eye;
        double radius = world.TargetRadiusKilometers(target);
        if (offset.Length <= radius) return null;
        if (target == SolarSystemSample.SunId) return -26.74 + 5 * Math.Log10(offset.Length / au);
        var toSun = sun.PositionKilometers - body.PositionKilometers;
        if (!(toSun.Length > 0)) return null;
        double phase = Math.Acos(Math.Clamp(-Vector3d.Dot(toSun / toSun.Length, offset / offset.Length), -1, 1));
        double lambert = (Math.Sin(phase) + (Math.PI - phase) * Math.Cos(phase)) / Math.PI;
        double albedo = target == SolarSystemSample.MoonId ? .12 : .3;
        double flux = albedo * radius * radius / (offset.Length * offset.Length) * lambert * au * au / (toSun.Length * toSun.Length);
        return flux > 0 ? -26.74 - 2.5 * Math.Log10(flux) : null;
    }
}
