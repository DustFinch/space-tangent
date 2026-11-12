// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Simulation;

internal sealed class WorldOrbitAtmosphere
{
    private readonly MeshInstance3D _globe, _shell;
    private readonly StandardMaterial3D _surface;
    private readonly ShaderMaterial _ground, _limb;
    private readonly OrbitAtmosphereTextures? _lookup;

    internal WorldOrbitAtmosphere(MeshInstance3D globe, float radius, OrbitAtmosphereTextures? lookup)
    {
        _globe = globe;
        _surface = (StandardMaterial3D)globe.MaterialOverride;
        _lookup = lookup;
        _ground = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader") };
        _limb = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_limb.gdshader") };
        float shellHeight = lookup?.HeightKilometers ?? 105;
        float groundRadius = lookup?.RadiusKilometers ?? radius;
        foreach (ShaderMaterial material in new[] { _ground, _limb })
        {
            material.SetShaderParameter("ground_radius_km", groundRadius);
            material.SetShaderParameter("shell_height_km", shellHeight);
            material.SetShaderParameter("air_base_km", 70f);
            lookup?.Bind(material);
        }
        _ground.SetShaderParameter("has_globe_map", _surface.AlbedoTexture is not null);
        _ground.SetShaderParameter("globe_color", _surface.AlbedoColor);
        if (_surface.AlbedoTexture is { } map)
            _ground.SetShaderParameter("globe_map", map);
        _shell = new MeshInstance3D
        {
            Mesh = globe.Mesh,
            Scale = Vector3.One * (1 + shellHeight / groundRadius),
            MaterialOverride = _limb,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        _globe.AddChild(_shell);
    }

    internal bool Active => _globe.Visible && _shell.Visible;
    internal bool UsesLookup => _lookup is not null;
    internal bool Textured => _surface.AlbedoTexture is not null;

    internal void Update(Vector3 light, Vector3 eye)
    {
        float radius = _globe.Scale.X;
        bool close = radius > .1f && radius > eye.DistanceTo(_globe.Position) * .007f;
        if (!close)
        {
            _globe.MaterialOverride = _surface;
            _shell.Hide();
            return;
        }
        var local = _globe.Transform.Basis.Orthonormalized().Inverse() * light;
        _ground.SetShaderParameter("light_in_body", local);
        _limb.SetShaderParameter("light_in_body", local);
        _globe.MaterialOverride = _ground;
        _shell.Show();
    }
}
