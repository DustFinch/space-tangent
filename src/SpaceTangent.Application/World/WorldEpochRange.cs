// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

internal static class WorldEpochRange
{
    internal static bool Contains(ISolarSystemEphemeris ephemeris, JulianInstant epoch) =>
        epoch.Scale == TimeScale.Tdb && epoch.CompareTo(ephemeris.Start) >= 0 && epoch.CompareTo(ephemeris.End) <= 0;
}
