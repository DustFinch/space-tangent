// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SpaceTangent.Core.Surfaces;

namespace SpaceTangent.Application.Addons;

public sealed record DecodedBodyTerrain(int Width, int Height, float RadiusKilometers, byte[] Meters)
{
    public static DecodedBodyTerrain Load(string path, double expectedRadius, CancellationToken cancellation)
    {
        ElevationMap map = ElevationMap.Load(path, cancellation);
        if (Math.Abs(map.ReferenceRadiusKilometers - expectedRadius) > 0.001)
            throw new InvalidDataException("The terrain datum does not match the reference radius.");
        var bytes = new byte[checked(map.Width * map.Height * sizeof(float))];
        map.CopyHeightsMeters(MemoryMarshal.Cast<byte, float>(bytes), cancellation);
        return new(map.Width, map.Height, (float)map.ReferenceRadiusKilometers, bytes);
    }
}
