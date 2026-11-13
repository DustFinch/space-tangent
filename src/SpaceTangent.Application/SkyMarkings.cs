// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

[Flags]
public enum SkyMarkings
{
    None = 0,
    GalacticEquator = 2,
    Meridian = 4,
    PrimeVertical = 8,
    PrecessionCircles = 32,
    CircumpolarCircles = 64,
    ZenithAndNadir = 512
}
