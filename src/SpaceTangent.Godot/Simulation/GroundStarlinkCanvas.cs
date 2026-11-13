// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal partial class GroundStarlinkCanvas : Control
{
    private readonly List<(Vector2 Pixel, float Strength)> _points = [];
    private readonly List<(LocalTleSatellite Satellite, CartesianState State)> _states = [];
    private SimulationWorld? _world;
    private LocalTleSatellite[] _members = [];
    private (JulianInstant Epoch, DeltaTModel DeltaT)? _sample;
    internal int PointCount => _points.Count;

    internal void UpdatePoints(SimulationWorld world, SolarSystemSample solar, DeltaTModel deltaT, SurfaceLighting light,
        EarthObserverCamera observer, Vector3 forward, Vector3 right, Vector3 up, float tangent, Vector3 moon, float moonRadius,
        float darkness, double limit, IReadOnlyDictionary<ObjectId, double> magnitudes)
    {
        _points.Clear();
        if (!ReferenceEquals(_world, world))
        {
            _world = world;
            _members = world.LocalSatellites.Where(StarlinkPhotometry.IsMember).ToArray();
            _sample = null;
        }
        if (darkness > 0 && Size.X > 1 && Size.Y > 1)
        {
            if (_sample != (solar.Epoch, deltaT))
            {
                _states.Clear();
                foreach (var satellite in _members)
                    if (satellite.SampleInWorld(solar.Epoch, deltaT) is { } state)
                        _states.Add((satellite, state));
                _sample = (solar.Epoch, deltaT);
            }

            Vector3d sun = light.EarthToSun / light.EarthToSun.Length;
            var view = new Rect2(Vector2.Zero, Size).Grow(3);
            foreach (var (satellite, state) in _states)
            {
                Vector3d geometric = state.PositionKilometers - light.PositionKilometers;
                if (Vector3d.Dot(geometric, light.EastNorthUp.Z) < 0)
                    continue;
                double standard = magnitudes.TryGetValue(satellite.Id, out double value) ? value : 5.5;
                if (StarlinkPhotometry.Magnitude(state.PositionKilometers, light.PositionKilometers, sun, standard) is not { } magnitude)
                    continue;
                var offset = observer.ApparentPlaces
                    ? ApparentSky.BodyOffset(state + solar.Earth, solar.Earth, light.PositionKilometers) : geometric;
                var direction = offset / offset.Length;
                direction = observer.Conditions?.RefractDirection(direction, light.EastNorthUp.Z, observer.Site) ?? direction;
                double elevation = Math.Asin(Math.Clamp(Vector3d.Dot(direction, light.EastNorthUp.Z), -1, 1)) * 180 / Math.PI;
                magnitude += observer.Conditions?.AttenuationMagnitude(elevation) ?? 0;
                var ray = BrightSkyOverlay.Convert(direction);
                if (magnitude > limit || ray.Dot(moon) >= MathF.Cos(moonRadius))
                    continue;
                float depth = ray.Dot(forward);
                if (depth <= 0)
                    continue;
                float scale = Size.Y / (2 * depth * tangent);
                Vector2 pixel = Size / 2 + new Vector2(ray.Dot(right), -ray.Dot(up)) * scale;
                if (view.HasPoint(pixel))
                    _points.Add((pixel, darkness * (float)Math.Clamp(.06 * Math.Pow(10, -.2 * (magnitude - 5.5)), .006, .8)));
            }
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var (pixel, strength) in _points)
            DrawCircle(pixel, 1.6f, new Color(strength, strength, strength, 1), antialiased: true);
    }
}
