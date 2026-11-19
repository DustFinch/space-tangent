// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

/// <summary>The J2000 mean ecliptic of the supported external formats, sky overlays and catalogue orbits.</summary>
/// <remarks>
/// The value is the IAU 1976 obliquity at J2000, 84381.448″, used by the imported URLs, favorites and catalogues.
/// The IAU 2006 value, 84381.406″, differs by 0.042″. Earth orientation itself comes from
/// <see cref="SpaceTangent.Core.Coordinates.EarthOrientation"/>.
/// </remarks>
public static class EclipticJ2000
{
    public const double ObliquityDegrees = 23.4392911;
    public const double ObliquityRadians = ObliquityDegrees * Math.PI / 180;
}
