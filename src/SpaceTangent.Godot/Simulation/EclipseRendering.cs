// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal static class EclipseRendering
{
    internal static void SetEarthShadow(ShaderMaterial material, SurfaceLighting lighting, bool godotAxes)
    {
        // An expanded Moon bounds the possible shadow for all receivers within the Earth's atmosphere.
        const double marginKilometers = 160;
        bool active = SolarOccultation.VisibleFraction(lighting.EarthToSun, lighting.EarthToMoon,
            EarthSurfaceLighting.EarthRadiusKilometers + EarthSurfaceLighting.MoonRadiusKilometers + marginKilometers) < 1;
        material.SetShaderParameter("solar_eclipse_active", active);
        if (!active)
            return;
        material.SetShaderParameter("eclipse_sun_km", Axis(lighting.EarthToSun));
        material.SetShaderParameter("eclipse_moon_km", Axis(lighting.EarthToMoon));

        Vector3 Axis(Vector3d vector) => godotAxes
            ? new((float)vector.X, (float)vector.Z, (float)-vector.Y)
            : new((float)vector.X, (float)vector.Y, (float)vector.Z);
    }

    internal static double MoonSunFraction(SolarSystemSample sample) => SolarOccultation.VisibleFraction(
        (sample.Sun - sample.Moon).PositionKilometers, (sample.Earth - sample.Moon).PositionKilometers,
        EarthSurfaceLighting.EarthRadiusKilometers);
}
