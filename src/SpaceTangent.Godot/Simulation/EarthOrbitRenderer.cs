// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed record EarthOrbitTextures(Texture2D Day, Texture2D? Night, Texture2D? Clouds)
{
    internal static EarthOrbitTextures? Load(SurfaceTextures? surface)
    {
        string root = LocalDataPaths.Find("SPACETANGENT_PLANET_TEXTURES", "planets", directory: true);
        Texture2D? day = PlanetTextures.Read(root, "earth.jpg");
        return day is null ? null : new(day, PlanetTextures.Read(root, "earth-night.jpg"), surface?.Clouds);
    }
}

internal sealed class EarthOrbitRenderer
{
    private const double Radius = EarthSurfaceLighting.EarthRadiusKilometers;
    private const double AirTop = 100;
    private readonly MeshInstance3D _globe, _shell;
    private readonly Material? _fallback;
    private readonly ShaderMaterial _ground, _air;

    internal EarthOrbitRenderer(MeshInstance3D globe, EarthOrbitTextures textures)
    {
        _globe = globe;
        _fallback = globe.MaterialOverride;
        _ground = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_orbit.gdshader") };
        _ground.SetShaderParameter("earth_radius_km", Radius);
        _ground.SetShaderParameter("day_map", textures.Day);
        _ground.SetShaderParameter("has_night_map", textures.Night is not null);
        _ground.SetShaderParameter("has_cloud_map", textures.Clouds is not null);
        if (textures.Night is { } night)
            _ground.SetShaderParameter("night_map", night);
        if (textures.Clouds is { } clouds)
            _ground.SetShaderParameter("cloud_map", clouds);
        _air = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_limb.gdshader") };
        _air.SetShaderParameter("earth_radius_km", Radius);
        _shell = new MeshInstance3D
        {
            Mesh = globe.Mesh, MaterialOverride = _air, Visible = false,
            Scale = Vector3.One * (float)(1 + AirTop / Radius),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _globe.AddChild(_shell);
    }

    internal bool Active => _globe.MaterialOverride == _ground && _shell.Visible;

    internal void Update(SolarSystemSample sample, DeltaTModel deltaT, bool useAtmosphere)
    {
        SurfaceLighting light;
        try
        {
            light = EarthSurfaceLighting.Sample(sample, new(0, 0, 0), deltaT);
        }
        catch (ArgumentOutOfRangeException)
        {
            _globe.MaterialOverride = _fallback;
            _shell.Hide();
            return;
        }
        Vector3 sun = Axis(light.EarthToSun).Normalized();
        _ground.SetShaderParameter("sun_direction", sun);
        _ground.SetShaderParameter("moon_direction", Axis(light.EarthToMoon).Normalized());
        _ground.SetShaderParameter("earth_angle", (float)light.EarthRotationRadians);
        _ground.SetShaderParameter("lunar_strength", (float)Math.Clamp(.12 * light.RelativeLunarFlux, 0, 1));
        _ground.SetShaderParameter("show_atmosphere", useAtmosphere);
        _air.SetShaderParameter("sun_direction", sun);
        _globe.MaterialOverride = _ground;
        _shell.Visible = useAtmosphere;

        static Vector3 Axis(Vector3d value) => new((float)value.X, (float)value.Z, (float)-value.Y);
    }
}
