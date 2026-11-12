// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private sealed record DemoGlobe(MeshInstance3D Globe, ShaderMaterial Material, MeshInstance3D? Rings, ShaderMaterial? RingMaterial)
    {
        internal DemoSurfaceCache.Maps? Maps { get; set; }
        internal MeshInstance3D? Atmosphere { get; init; }
    }

    internal DemoSurfaceCache? DemoSurfaces { get; init; }
    private readonly Dictionary<ObjectId, DemoGlobe> _demoGlobes = new();
    internal bool DemoWorldVisible(ObjectId id) => _demoGlobes.GetValueOrDefault(id)?.Globe.Visible == true;
    internal bool DemoWorldTextured(ObjectId id) => _demoGlobes.GetValueOrDefault(id)?.Maps is not null;
    internal bool DemoAirVisible(ObjectId id) => _demoGlobes.GetValueOrDefault(id)?.Atmosphere?.Visible == true;
    internal bool DemoRingsVisible(ObjectId id) => _demoGlobes.GetValueOrDefault(id)?.Rings?.Visible == true;

    private void DrawDemoWorlds(NavigationCamera pose, WorldSnapshot snapshot, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        if (pose.Target is not { } target || DemoWorlds.Find(target) is null)
            return;
        bool binary = IsBinary(target);
        foreach (DemoWorldBody body in DemoWorlds.Bodies)
        {
            if (IsBinary(body.Id) != binary || !snapshot.TryState(body.Id, out var state) ||
                !snapshot.TryOrientation(body.Id, out var orientation))
                continue;
            var (center, projection) = project(state.PositionKilometers);
            float radius = (float)(body.RadiusKilometers * projection / units);
            float distance = center.DistanceTo(_navigationCamera.Position);
            bool star = body.LuminositySolar > 0;
            if (_navigationCamera.IsPositionBehind(center) ||
                (body.Id != target && !star && radius < Math.Max(.001f, distance * .00014f)))
                continue;
            if (!_demoGlobes.TryGetValue(body.Id, out DemoGlobe? visual))
                _demoGlobes.Add(body.Id, visual = CreateDemoGlobe(body));
            if (star)
            {
                visual.Globe.Transform = new(Basis.FromScale(Vector3.One * Math.Max(radius, distance * .0011f)), center);
                visual.Globe.Show();
                continue;
            }

            Basis rotation = PlanetBasis(orientation.ToWorld);
            float pole = (float)(body.PolarRadiusKilometers / body.RadiusKilometers);
            visual.Globe.Transform = new(rotation * Basis.FromScale(new(radius, radius * pole, radius)), center);
            if (visual.Maps is null && radius > distance * .002f && DemoSurfaces?.Get(body) is { } maps)
            {
                visual.Maps = maps;
                visual.Material.SetShaderParameter("albedo_map", maps.Albedo);
                visual.Material.SetShaderParameter("normal_map", maps.Normal);
                visual.Material.SetShaderParameter("has_maps", true);
                if (maps.Clouds is { } clouds)
                    visual.Material.SetShaderParameter("cloud_map", clouds);
                if (maps.Emission is { } emission)
                    visual.Material.SetShaderParameter("emission_map", emission);
                if (maps.Rings is { } rings)
                {
                    visual.Material.SetShaderParameter("rings_map", rings);
                    visual.RingMaterial?.SetShaderParameter("rings_map", rings);
                }
                visual.Material.SetShaderParameter("has_clouds", maps.Clouds is not null);
                visual.Material.SetShaderParameter("has_emission", maps.Emission is not null);
                visual.Material.SetShaderParameter("has_rings", maps.Rings is not null);
            }
            var lights = DemoWorlds.Lights(snapshot, body.Id);
            Basis3d inertialToFixed = orientation.ToWorld.Transpose();
            if (visual.Atmosphere is { } shell && body.Atmosphere is { } atmosphere && radius > distance * .01f)
            {
                float height = radius * (float)(atmosphere.HeightKilometers / body.RadiusKilometers);
                shell.Transform = new(rotation * Basis.FromScale(new(radius + height, radius * pole + height, radius + height)), center);
                shell.Show();
            }
            SetLight("first", 0);
            SetLight("second", 1);
            visual.Globe.Show();
            if (visual.Rings is { } ring && visual.RingMaterial is { } ringMaterial && visual.Maps?.Rings is not null && lights.Count != 0)
            {
                ring.Transform = new(rotation * Basis.FromScale(Vector3.One * radius), center);
                ringMaterial.SetShaderParameter("sun_in_body", ToGodot(inertialToFixed.Transform(lights[0].Direction), 1));
                ringMaterial.SetShaderParameter("solar_radius_radians", (float)lights[0].AngularRadius);
                ring.Show();
            }

            void SetLight(string prefix, int index)
            {
                bool present = index < lights.Count;
                Vector3 direction = present ? ToGodot(inertialToFixed.Transform(lights[index].Direction), 1) : Vector3.Right;
                Vector3 irradiance = present ? ToGodotColor(lights[index].Color * lights[index].RelativeFlux) : Vector3.Zero;
                visual.Material.SetShaderParameter(prefix + "_direction", direction);
                visual.Material.SetShaderParameter(prefix + "_irradiance", irradiance);
                if (visual.Atmosphere is { Visible: true, MaterialOverride: ShaderMaterial air })
                {
                    air.SetShaderParameter(prefix + "_direction", direction);
                    air.SetShaderParameter(prefix + "_irradiance", irradiance);
                }
            }
        }
    }

    private DemoGlobe CreateDemoGlobe(DemoWorldBody body)
    {
        bool star = body.LuminositySolar > 0;
        var sphere = CreateNavigationSphere(Colors.White, star);
        sphere.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        var material = new ShaderMaterial
        {
            Shader = star ? _stellarShader : GD.Load<Shader>("res://Shaders/demo_globe.gdshader")
        };
        sphere.MaterialOverride = material;
        Vector3 color = ToGodotColor(body.Color);
        if (star)
        {
            material.SetShaderParameter("photosphere", new Color(color.X, color.Y, color.Z));
            return new(sphere, material, null, null);
        }
        material.SetShaderParameter("fallback_color", color);
        material.SetShaderParameter("cloud_height", (float)(body.CloudTopKilometers / body.RadiusKilometers));
        MeshInstance3D? shell = null;
        if (body.Atmosphere is { } atmosphere)
        {
            shell = CreateNavigationSphere(Colors.White);
            shell.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            var air = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/demo_air.gdshader") };
            shell.MaterialOverride = air;
            SetAtmosphere(material, body, atmosphere);
            SetAtmosphere(air, body, atmosphere);
        }
        if (body.RingOuterKilometers <= 0)
            return new(sphere, material, null, null) { Atmosphere = shell };
        float inner = (float)(body.RingInnerKilometers / body.RadiusKilometers);
        float outer = (float)(body.RingOuterKilometers / body.RadiusKilometers);
        material.SetShaderParameter("ring_inner", inner);
        material.SetShaderParameter("ring_outer", outer);
        var ringMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/planet_rings.gdshader") };
        ringMaterial.SetShaderParameter("polar_ratio", (float)(body.PolarRadiusKilometers / body.RadiusKilometers));
        var ring = new MeshInstance3D { Mesh = PlanetRingRendering.RingMesh(inner, outer, ringMaterial),
            Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(ring);
        return new(sphere, material, ring, ringMaterial) { Atmosphere = shell };
    }

    private void HideDemoWorlds()
    {
        foreach (DemoGlobe visual in _demoGlobes.Values)
        {
            visual.Globe.Hide();
            visual.Rings?.Hide();
            visual.Atmosphere?.Hide();
        }
    }

    private static void SetAtmosphere(ShaderMaterial material, DemoWorldBody body, DemoAtmosphere atmosphere)
    {
        material.SetShaderParameter("demo_radius", (float)body.RadiusKilometers);
        material.SetShaderParameter("demo_air_top", (float)atmosphere.HeightKilometers);
        material.SetShaderParameter("demo_rayleigh", ToGodotColor(atmosphere.RayleighPerKilometer));
        material.SetShaderParameter("demo_absorption", ToGodotColor(atmosphere.AbsorptionPerKilometer));
        material.SetShaderParameter("demo_mie_albedo", ToGodotColor(atmosphere.MieAlbedo ?? new(1, 1, 1)));
        material.SetShaderParameter("demo_mie", (float)atmosphere.MiePerKilometer);
        material.SetShaderParameter("demo_mie_g", (float)atmosphere.Asymmetry);
        material.SetShaderParameter("demo_rayleigh_height", (float)atmosphere.RayleighHeightKilometers);
        material.SetShaderParameter("demo_mie_height", (float)atmosphere.MieHeightKilometers);
    }

    private static bool IsBinary(ObjectId id) => id == DemoWorlds.TesseraA || id == DemoWorlds.TesseraB ||
        id == DemoWorlds.Nova || id == DemoWorlds.Selena;

    private static Vector3 ToGodotColor(Vector3d color) => new((float)color.X, (float)color.Y, (float)color.Z);
}
