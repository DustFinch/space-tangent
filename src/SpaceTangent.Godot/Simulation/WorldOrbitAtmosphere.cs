// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed class WorldOrbitAtmosphere
{
    private readonly MeshInstance3D _globe;
    private readonly MeshInstance3D? _shell;
    private readonly StandardMaterial3D _surface;
    private readonly ShaderMaterial _ground;
    private readonly ShaderMaterial? _limb;
    private readonly OrbitAtmosphereTextures? _lookup;
    private readonly MeshInstance3D? _clouds;
    private readonly ShaderMaterial? _cloudMaterial;
    private const float VenusCloudHeight = 65;
    private static readonly float VenusAtmosphereHeight = -10f * MathF.Log(.000125f);
    private const double VenusCloudDegreesPerDay = 90;
    internal BodyTileTextures? Tiles { get; }

    internal WorldOrbitAtmosphere(MeshInstance3D globe, float radius, OrbitAtmosphereTextures? lookup,
        BodyTileTextures? tiles, bool hasAtmosphere, Texture2D? venusClouds = null, bool venusScattering = false)
    {
        _globe = globe;
        _surface = (StandardMaterial3D)globe.MaterialOverride;
        _lookup = lookup;
        Tiles = tiles;
        _ground = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader") };
        _ground.SetShaderParameter("has_globe_map", _surface.AlbedoTexture is not null);
        _ground.SetShaderParameter("globe_color", _surface.AlbedoColor);
        _ground.SetShaderParameter("prime_meridian_offset", .25f);
        if (_surface.AlbedoTexture is { } map)
            _ground.SetShaderParameter("globe_map", map);
        Tiles?.Bind(_ground);
        if (!hasAtmosphere)
            return;

        _limb = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_limb.gdshader") };
        float shellHeight = lookup?.HeightKilometers ?? (venusScattering ? VenusAtmosphereHeight : 105);
        float groundRadius = lookup?.RadiusKilometers ?? radius;
        foreach (ShaderMaterial material in new[] { _ground, _limb })
        {
            material.SetShaderParameter("ground_radius_km", groundRadius);
            material.SetShaderParameter("shell_height_km", shellHeight);
            material.SetShaderParameter("air_base_km", 70f);
            material.SetShaderParameter("venus_scattering", venusScattering);
            lookup?.Bind(material);
        }
        Material shellMaterial = _limb;
        if (venusScattering)
        {
            _limb.RenderPriority = -1;
            var transmission = new ShaderMaterial
            {
                Shader = GD.Load<Shader>("res://Shaders/venus_orbit_transmission.gdshader"),
                RenderPriority = -2,
                NextPass = _limb
            };
            transmission.SetShaderParameter("ground_radius_km", groundRadius);
            transmission.SetShaderParameter("shell_height_km", shellHeight);
            shellMaterial = transmission;
        }

        _shell = new MeshInstance3D
        {
            Mesh = globe.Mesh,
            Scale = Vector3.One * (1 + shellHeight / groundRadius),
            MaterialOverride = shellMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _globe.AddChild(_shell);
        if (venusClouds is not null)
        {
            _cloudMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/venus_orbit_clouds.gdshader") };
            _cloudMaterial.SetShaderParameter("cloud_map", venusClouds);
            _cloudMaterial.SetShaderParameter("cloud_radius_km", groundRadius + VenusCloudHeight);
            _cloudMaterial.SetShaderParameter("ground_radius_km", groundRadius);
            _cloudMaterial.SetShaderParameter("shell_height_km", shellHeight);
            _cloudMaterial.SetShaderParameter("venus_scattering", venusScattering);
            _clouds = new()
            {
                Mesh = globe.Mesh,
                Scale = Vector3.One * (1 + VenusCloudHeight / groundRadius),
                MaterialOverride = _cloudMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false
            };
            _globe.AddChild(_clouds);
        }
    }

    internal bool Active => _globe.Visible && _shell?.Visible == true;
    internal bool UsesLookup => _lookup is not null;
    internal bool Textured => _surface.AlbedoTexture is not null || Tiles?.Color is not null;

    internal (bool Visible, float Offset)? CloudState => _clouds is null ? null :
        (_clouds.IsVisibleInTree(), _cloudMaterial!.GetShaderParameter("cloud_offset").AsSingle());

    internal void Update(Vector3d sunward, Vector3 eye, JulianInstant epoch,
        bool showAtmosphere, bool showClouds, Vector3d occluder = default, double occluderRadius = 0)
    {
        float radius = _globe.Scale.X;
        float distance = eye.DistanceTo(_globe.Position);
        bool near = radius > .1f && radius > distance * .007f;
        bool showAir = showAtmosphere && _shell is not null && near;
        bool showDeck = showClouds && _clouds is not null && near && distance >= radius * _clouds.Scale.X;
        if (_clouds is not null)
            _clouds.Visible = showDeck;
        if (_shell is not null)
            _shell.Visible = showAir;
        if (!showAir && !showDeck && Tiles is null)
        {
            _globe.MaterialOverride = _surface;
            return;
        }
        Basis bodyFromWorld = _globe.Transform.Basis.Orthonormalized().Inverse();
        Vector3 local = BodyVector(sunward / sunward.Length);
        SetOccultation(_ground);
        if (showAir)
            SetOccultation(_limb!);

        Vector3 BodyVector(Vector3d vector) => bodyFromWorld * new Vector3((float)vector.X, (float)vector.Z, (float)-vector.Y);

        void SetOccultation(ShaderMaterial material)
        {
            material.SetShaderParameter("occulting_radius_km", (float)occluderRadius);
            if (occluderRadius <= 0)
                return;
            material.SetShaderParameter("to_occluder_km", BodyVector(occluder));
            material.SetShaderParameter("to_sun_km", BodyVector(sunward));
        }
        if (showDeck)
        {
            _cloudMaterial!.SetShaderParameter("draw_air", showAir);
            double turns = epoch.ToJulianDate() * VenusCloudDegreesPerDay / 360;
            _cloudMaterial!.SetShaderParameter("cloud_offset", (float)-(turns - Math.Floor(turns)));
            _cloudMaterial.SetShaderParameter("light_in_body", local);
        }
        _ground.SetShaderParameter("light_in_body", local);
        _ground.SetShaderParameter("draw_air", showAir);
        if (showAir)
            _limb!.SetShaderParameter("light_in_body", local);
        _globe.MaterialOverride = _ground;
    }
}
