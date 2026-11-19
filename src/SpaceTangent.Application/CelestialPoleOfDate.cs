// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Application;

/// <summary>Mean pole of date in world axes, using the same precession model as terrestrial observers.</summary>
public static class CelestialPoleOfDate
{
    public static Vector3d? Direction(JulianInstant epoch) =>
        epoch.Scale == TimeScale.Tdb && Math.Abs(epoch.ToJulianDate() - JulianInstant.J2000Day) <= 800000
            ? EarthOrientation.MeanEquatorOfDateToWorld(epoch).Z : null;
}
