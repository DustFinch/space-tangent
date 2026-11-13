// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

[Flags]
public enum SkyMarkings
{
    None = 0,
    EquatorOfDate = 1,
    GalacticEquator = 2,
    Meridian = 4,
    PrimeVertical = 8,
    Colures = 16,
    PrecessionCircles = 32,
    CircumpolarCircles = 64,
    Poles = 128,
    EquinoxesAndSolstices = 256,
    ZenithAndNadir = 512,
    ArchaeoLines = 1024,
    NavigationalStars = 2048,
    Pulsars = 4096,
    Quasars = 8192
}
