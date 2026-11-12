// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

/// <summary>The legacy perspective zoom measured against the screen's physical vertical angle.</summary>
public static class PerspectiveViewScale
{
    public const int DefaultDpi = 96, DistanceMillimeters = 400;

    public static double ScreenAngleDegrees(double heightPixels, int dpi)
    {
        if (!(heightPixels > 0) || !double.IsFinite(heightPixels))
            throw new ArgumentOutOfRangeException(nameof(heightPixels));
        if (dpi <= 0)
            dpi = DefaultDpi;
        return 2 * Math.Atan(heightPixels * 25.4 / (2 * dpi * DistanceMillimeters)) * 180 / Math.PI;
    }
}
