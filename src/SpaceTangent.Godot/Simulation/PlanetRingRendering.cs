// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal readonly record struct PlanetRingProfile(ObjectId Id, string Texture, float InnerKilometers, float OuterKilometers);

internal sealed class PlanetRingRendering
{
    internal static readonly PlanetRingProfile[] Profiles =
    [
        new(new("sol/jupiter"), "jupiter-rings.png", 122_500, 129_360),
        new(new("sol/saturn"), "saturn-rings.png", 74_510, 140_245),
        new(new("sol/uranus"), "uranus-rings.png", 37_812, 52_392),
        new(new("sol/neptune"), "neptune-rings.png", 40_900, 62_947)
    ];

    private readonly MeshInstance3D _globe;
    private readonly MeshInstance3D _rings;
    internal MeshInstance3D? Atmosphere { get; }
    private readonly ShaderMaterial _surface, _ring;

    internal PlanetRingRendering(Node3D scene, MeshInstance3D globe, PlanetRingProfile profile, Texture2D texture,
        double radiusKilometers)
    {
        _globe = globe;
        var original = (StandardMaterial3D)globe.MaterialOverride;
        _surface = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/ringed_globe.gdshader") };
        _ring = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/planet_rings.gdshader") };
        float inner = profile.InnerKilometers / (float)radiusKilometers;
        float outer = profile.OuterKilometers / (float)radiusKilometers;
        _surface.SetShaderParameter("rings_map", texture);
        _surface.SetShaderParameter("ring_inner", inner);
        _surface.SetShaderParameter("ring_outer", outer);
        _surface.SetShaderParameter("surface_color", original.AlbedoColor);
        _surface.SetShaderParameter("prime_meridian_offset", original.Uv1Offset.X);
        Textured = original.AlbedoTexture is not null;
        _surface.SetShaderParameter("has_surface_map", Textured);
        if (original.AlbedoTexture is { } map)
            _surface.SetShaderParameter("surface_map", map);
        _ring.SetShaderParameter("rings_map", texture);
        globe.MaterialOverride = _surface;

        _rings = new MeshInstance3D { Mesh = RingMesh(inner, outer, _ring), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        scene.AddChild(_rings);
        if (profile.Id == PlanetCatalogue.SaturnId)
        {
            float shellRatio = 1 + (float)(34 * -Math.Log(.000125) / radiusKilometers);
            var air = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/saturn_ringed_limb.gdshader") };
            air.SetShaderParameter("radius_km", (float)radiusKilometers);
            air.SetShaderParameter("shell_ratio", shellRatio);
            air.SetShaderParameter("rings_map", texture);
            air.SetShaderParameter("ring_inner", inner);
            air.SetShaderParameter("ring_outer", outer);
            Atmosphere = new()
            {
                Mesh = globe.Mesh, Scale = Vector3.One * shellRatio,
                MaterialOverride = air, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            globe.AddChild(Atmosphere);
        }
    }

    internal static ImmediateMesh RingMesh(float inner, float outer, Material material)
    {
        var mesh = new ImmediateMesh();
        const int segments = 192;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, material);
        for (int step = 0; step <= segments; step++)
        {
            float angle = Mathf.Tau * step / segments;
            Vector3 radial = new(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            mesh.SurfaceSetUV(new(0, .5f));
            mesh.SurfaceAddVertex(radial * inner);
            mesh.SurfaceSetUV(new(1, .5f));
            mesh.SurfaceAddVertex(radial * outer);
        }
        mesh.SurfaceEnd();
        return mesh;
    }

    internal bool Textured { get; }
    internal bool Visible => _rings.Visible;
    internal void Hide()
    {
        _rings.Hide();
        Atmosphere?.Hide();
    }

    internal void Update(Vector3 sunDirection, double sunDistanceKilometers, bool bodyFixed)
    {
        Basis rotation = _globe.Transform.Basis.Orthonormalized();
        Vector3 light = rotation.Inverse() * sunDirection;
        float radius = _globe.Scale.X;
        float pole = _globe.Scale.Y / radius;
        bool visible = bodyFixed && radius > .12f;
        _surface.SetShaderParameter("sun_in_body", light);
        _surface.SetShaderParameter("polar_ratio", pole);
        _surface.SetShaderParameter("cast_ring_shadows", visible);
        if (Atmosphere is { MaterialOverride: ShaderMaterial air } shell)
        {
            shell.Visible = visible;
            if (visible)
            {
                air.SetShaderParameter("sun_in_body", light);
                air.SetShaderParameter("polar_ratio", pole);
                air.SetShaderParameter("cast_ring_shadows", true);
            }
        }
        if (!visible)
            return;
        _ring.SetShaderParameter("sun_in_body", light);
        _ring.SetShaderParameter("polar_ratio", pole);
        _ring.SetShaderParameter("solar_radius_radians",
            (float)Math.Asin(Math.Min(1, SolarOccultation.SunRadiusKilometers / sunDistanceKilometers)));
        _rings.Transform = new(rotation * Basis.FromScale(Vector3.One * radius), _globe.Position);
        _rings.Show();
    }
}
