// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private const int MaximumGroundPlanets = 7;
    private readonly Vector4[] _planetDiscs = new Vector4[MaximumGroundPlanets];
    private readonly Vector3[] _planetIllumination = new Vector3[MaximumGroundPlanets];
    private readonly bool _hasGroundRings;
    private readonly Vector4[] _planetPoles = new Vector4[MaximumGroundPlanets];
    private readonly Vector3[] _planetRings = new Vector3[MaximumGroundPlanets];
    private static readonly int[] GroundRingRows = PlanetCatalogue.Planets
        .Select(planet => Array.FindIndex(PlanetRingRendering.Profiles, profile => profile.Id == planet.Id)).ToArray();
    private readonly Vector3[] _planetColors = new Vector3[MaximumGroundPlanets];
    private static readonly Color[] GroundPlanetColors = PlanetCatalogue.Planets
        .Select(planet => new Color(planet.Color).SrgbToLinear()).ToArray();

    private void DrawPlanets(WorldSnapshot? snapshot, SolarSystemSample sample, SurfaceLighting lighting,
        EarthObserverCamera observer, bool physicalScale, Vector3 forward, Vector3 right, Vector3 cameraUp)
    {
        int count = 0;
        if (snapshot is not null)
        {
            double scale = physicalScale ? 1 : observer.PlanetScale ?? 1;
            float aspect = _surface.Size.X / Math.Max(1, _surface.Size.Y);
            for (int index = 0; index < PlanetCatalogue.Planets.Count; index++)
            {
                var planet = PlanetCatalogue.Planets[index];
                if (planet.Id == SolarSystemSample.EarthId || !snapshot.TryState(planet.Id, out var state))
                    continue;
                var offset = (state - sample.Earth).PositionKilometers - lighting.PositionKilometers;
                double distance = offset.Length;
                if (distance <= planet.RadiusKilometers)
                    continue;
                var geometric = offset / distance;
                var apparent = observer.Conditions?.RefractDirection(geometric, lighting.EastNorthUp.Z, observer.Site) ?? geometric;
                float radius = (float)(scale * planet.RadiusKilometers / distance);
                int ringRow = _hasGroundRings ? GroundRingRows[index] : -1;
                float extent = radius;
                Vector3 pole = Vector3.Zero;
                var rings = Vector3.Zero;
                if (ringRow >= 0 && snapshot.TryOrientation(planet.Id, out var rotation))
                {
                    var profile = PlanetRingRendering.Profiles[ringRow];
                    rings = new(profile.InnerKilometers / (float)planet.RadiusKilometers,
                        profile.OuterKilometers / (float)planet.RadiusKilometers,
                        (ringRow + .5f) / PlanetRingRendering.Profiles.Length);
                    extent *= rings.Y;
                    pole = Convert(rotation.ToWorld.Z);
                }
                if (Vector3d.Dot(apparent, lighting.EastNorthUp.Z) < -extent)
                    continue;
                Vector3 center = Convert(apparent);
                float depth = center.Dot(forward);
                if (depth <= 0 || Math.Abs(center.Dot(right)) > depth * _tanHalfField * aspect + extent + .005f ||
                    Math.Abs(center.Dot(cameraUp)) > depth * _tanHalfField + extent + .005f)
                    continue;
                var sun = (sample.Sun - state).PositionKilometers;
                if (sun.Length == 0)
                    continue;
                var color = GroundPlanetColors[index];
                _planetDiscs[count] = new(center.X, center.Y, center.Z, radius);
                _planetIllumination[count] = Convert(sun / sun.Length);
                _planetPoles[count] = new(pole.X, pole.Y, pole.Z, (float)(planet.PolarRadiusKilometers / planet.RadiusKilometers));
                _planetRings[count] = rings;
                _planetColors[count++] = new(color.R, color.G, color.B);
            }
        }
        _material.SetShaderParameter("ground_planet_count", count);
        if (count > 0)
        {
            _material.SetShaderParameter("ground_planet_discs", _planetDiscs);
            _material.SetShaderParameter("ground_planet_sun", _planetIllumination);
            _material.SetShaderParameter("ground_planet_color", _planetColors);
            _material.SetShaderParameter("ground_planet_poles", _planetPoles);
            _material.SetShaderParameter("ground_planet_rings", _planetRings);
        }
    }
}
