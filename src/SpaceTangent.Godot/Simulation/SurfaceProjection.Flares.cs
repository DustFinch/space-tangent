// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private const int MaximumVisibleFlares = 8;
    private readonly Vector4[] _flareDirections = new Vector4[MaximumVisibleFlares];
    private SimulationWorld? _flareWorld;
    private LocalTleSatellite[] _flareSatellites = [];

    private void DrawSatelliteFlares(SimulationWorld? world, SurfaceLighting lighting, EarthObserverCamera observer,
        SolarSystemSample solar, DeltaTModel model, Vector3 forward, VisualOptics? optics)
    {
        if (!ReferenceEquals(world, _flareWorld))
        {
            _flareWorld = world;
            _flareSatellites = world?.LocalSatellites.Where(ObservationEventSearch.HasFlarePanels).ToArray() ?? [];
        }
        var epoch = solar.Epoch;
        int count = 0;
        double daylight = Math.Clamp((lighting.SolarAltitudeRadians / Degrees + 6) / 12, 0, 1);
        double limit = Math.Min(1 - daylight * 5, optics?.LimitingMagnitude() ?? observer.Conditions?.LimitingMagnitude() ?? 6.5);
        foreach (var satellite in _flareSatellites)
        {
            if (satellite.SampleInWorld(epoch, model) is not { } state ||
                ObservationEventSearch.EvaluateSatelliteFlare(lighting, state, epoch) is not { } flare ||
                flare.Magnitude > limit)
                continue;
            var offset = observer.ApparentPlaces
                ? ApparentSky.BodyOffset(state + solar.Earth, solar.Earth, lighting.PositionKilometers)
                : state.PositionKilometers - lighting.PositionKilometers;
            var direction = offset / offset.Length;
            var apparent = observer.Conditions?.RefractDirection(direction, lighting.EastNorthUp.Z, observer.Site) ?? direction;
            Vector3 ray = Convert(apparent);
            if (ray.Dot(forward) <= 0)
                continue;
            float strength = (float)Math.Clamp(.18 * Math.Pow(10, -.2 * flare.Magnitude), .06, 6);
            _flareDirections[count++] = new(ray.X, ray.Y, ray.Z, strength);
            if (count == MaximumVisibleFlares)
                break;
        }
        _material.SetShaderParameter("satellite_flare_count", count);
        if (count > 0)
            _material.SetShaderParameter("satellite_flares", _flareDirections);
    }
}
