// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly List<MeshInstance3D> _archivedBodies = [];
    private readonly Dictionary<string, MeshInstance3D> _archivedVisible = new(StringComparer.Ordinal);
    private readonly SphereMesh _archivedSphere = new() { Radius = 1, Height = 2, RadialSegments = 48, Rings = 24 };
    private Shader? _archivedBodyShader;
    private Texture2D? _archivedMoonMap;
    private EarthOrbitTextures? _archivedEarthMaps;
    internal MeshInstance3D? ArchivedEarthLimb { get; private set; }
    private int _archivedUsed;
    private readonly Vector4[] _archivedBlockers = new Vector4[4];
    private readonly List<MeshInstance3D> _archivedPointPool = [];
    private readonly Dictionary<string, MeshInstance3D> _archivedPoints = new(StringComparer.Ordinal);
    private int _archivedPointsUsed;

    internal MeshInstance3D? ArchivedPoint(string name) => _archivedPoints.GetValueOrDefault(name);
    private PlanetTextures? _archivedPlanetTextures;
    private readonly Dictionary<ObjectId, (MeshInstance3D Node, Vector2 Radii)> _archivedRings = new();

    internal MeshInstance3D? ArchivedRings(ObjectId id) => _archivedRings.GetValueOrDefault(id).Node;
    private IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> _archivedAtmosphereLookups =
        new Dictionary<ObjectId, OrbitAtmosphereTextures>();
    private readonly Dictionary<ObjectId, MeshInstance3D> _archivedAtmospheres = new();

    internal MeshInstance3D? ArchivedAtmosphere(ObjectId id) => _archivedAtmospheres.GetValueOrDefault(id);

    internal RecordedSceneGeometry? ArchivedGeometry { get; set; }
    internal MeshInstance3D? ArchivedBody(string name) => _archivedVisible.GetValueOrDefault(name);

    private void DrawArchivedBodies(double units)
    {
        HideArchivedBodies();
        if (ArchivedGeometry is not { } scene)
            return;

        _navigationSun.Hide();
        _navigationEarth.Hide();
        _navigationMoon.Hide();
        HidePlanetGlobes();
        HideImportedBodies();
        HideComets();
        HideLocalSatelliteMarkers();
        foreach (var (_, satellite) in _satellites.Values)
            satellite.Hide();

        Basis camera = _navigationCamera.Basis;
        double scale = scene.KilometersPerUnit / units;
        Plane[] frustum = _navigationCamera.GetFrustum().ToArray();
        foreach (var body in scene.Bodies)
        {
            if (body.CameraPosition.Z >= Math.Max(body.Radius, body.RingOuterRadius))
                continue;
            double distance = body.CameraPosition.Length * scale;
            double projection = distance > NavigationFar * .8 ? NavigationFar * .8 / distance : 1;
            float radius = (float)(body.Radius * scale * projection);

            Vector3 local = Column(body.CameraPosition * (scale * projection));
            Vector3 center = _navigationCamera.Position + camera * local;
            var rotation = body.CameraBasis;
            double maximumAxis = Math.Sqrt(Vector3d.Dot(rotation.X, rotation.X) + Vector3d.Dot(rotation.Y, rotation.Y) + Vector3d.Dot(rotation.Z, rotation.Z));
            double extent = Math.Max(body.Radius * maximumAxis, body.RingOuterRadius) * scale * projection;
            if (frustum.Any(plane => plane.DistanceTo(center) > extent))
                continue;

            if (scene.BodyPoints.TryGetValue(body.Name, out var point))
            {
                DrawArchivedPoint(point, center, radius, scene.StellarView);
                if (point.ReplacesSurface)
                    continue;
            }
            if (radius < .001f)
                continue;

            if (_archivedBodies.Count == _archivedUsed)
            {
                _archivedBodyShader ??= GD.Load<Shader>("res://Shaders/archived_body.gdshader");
                var mesh = new MeshInstance3D
                {
                    MaterialOverride = new ShaderMaterial { Shader = _archivedBodyShader },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                AddChild(mesh);
                _archivedBodies.Add(mesh);
            }
            var node = _archivedBodies[_archivedUsed++];
            _archivedVisible.TryAdd(body.Name, node);
            var basis = new Basis(Column(rotation.X), Column(rotation.Y) * (float)body.PolarScale, Column(rotation.Z));
            var material = (ShaderMaterial)node.MaterialOverride;
            var id = body.Id;
            var source = id.HasValue ? _planetGlobes.GetValueOrDefault(id.Value) : null;
            var globeMaterial = source?.MaterialOverride as StandardMaterial3D;
            // The archive's spherical surfaces use a quarter-turn about the pole.
            if (source?.Mesh is ArrayMesh model)
                node.Mesh = model;
            else
            {
                node.Mesh = _archivedSphere;
                basis *= new Basis(Vector3.Up, Mathf.Pi / 2);
            }
            node.Transform = new(camera * basis.Scaled(Vector3.One * radius), center);
            EarthOrbitTextures? earth = id == SolarSystemSample.EarthId ? _archivedEarthMaps : null;
            Texture2D? texture = id == SolarSystemSample.MoonId ? _archivedMoonMap : earth?.Day ?? (id is { } planet ? _archivedPlanetTextures?.Surfaces.GetValueOrDefault(planet) : null) ?? globeMaterial?.AlbedoTexture;
            material.SetShaderParameter("has_night_map", earth?.Night is not null);
            material.SetShaderParameter("has_cloud_map", earth?.Clouds is not null);
            if (earth?.Night is { } night)
                material.SetShaderParameter("night_map", night);
            if (earth?.Clouds is { } clouds)
                material.SetShaderParameter("cloud_map", clouds);
            material.SetShaderParameter("has_map", texture is not null);
            if (texture is not null)
                material.SetShaderParameter("surface_map", texture);
            material.SetShaderParameter("tint", id == SolarSystemSample.SunId ? new Color("ffd18d") :
                id == SolarSystemSample.EarthId ? new Color("4a76ad") : globeMaterial?.AlbedoColor ?? new Color("b8b0a6"));
            material.SetShaderParameter("emissive", id == SolarSystemSample.SunId);
            BindArchivedShadows(material, scene, body, radius);
            material.SetShaderParameter("camera_light", Column(body.LightDirection));
            material.SetShaderParameter("light_color", Column(body.LightColor));
            material.SetShaderParameter("camera_secondary_light", Column(body.SecondLightDirection));
            material.SetShaderParameter("secondary_light_color", Column(body.SecondLightColor));
            material.SetShaderParameter("cast_ring_shadows", false);
            node.Show();
            if (id is { } ringId && body.RingOuterRadius > body.RingInnerRadius &&
                _archivedPlanetTextures?.Rings.GetValueOrDefault(ringId) is { } ringMap)
            {
                Vector3 ringLight = camera * Column(body.LightDirection);
                if (node.Basis.Determinant() != 0)
                {
                    Vector3 localLight = node.Basis.Orthonormalized().Inverse() * ringLight;
                    material.SetShaderParameter("rings_map", ringMap);
                    material.SetShaderParameter("ring_inner", (float)(body.RingInnerRadius / body.Radius));
                    material.SetShaderParameter("ring_outer", (float)(body.RingOuterRadius / body.Radius));
                    material.SetShaderParameter("archived_ring_polar_ratio", (float)body.PolarScale);
                    material.SetShaderParameter("sun_in_body", localLight.Normalized());
                    material.SetShaderParameter("cast_ring_shadows", true);
                }
                DrawArchivedRings(ringId, node, body, scene, radius, ringLight, ringMap);
            }
            if (id is { } atmosphereId && body.AtmosphereRadius > body.Radius && body.LightDirection.Length > 0)
            {
                Vector3 light = camera * Column(body.LightDirection);
                if (atmosphereId == SolarSystemSample.EarthId)
                    DrawArchivedEarthLimb(node, body, scene, light);
                else
                    DrawArchivedWorldAtmosphere(atmosphereId, node, body, scene, light);
            }
        }

        DrawArchivedOverlays(scene, scale);
        DrawArchivedComets(scene.CometTails, scale);

        static Vector3 Column(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);
    }

    private void BindArchivedShadows(ShaderMaterial material, RecordedSceneGeometry scene, RecordedBodyGeometry body, float renderRadius, Basis? receiverAxes = null)
    {
        int count = 0;
        foreach (var blocker in scene.Shadows)
        {
            if (!string.Equals(blocker.Receiver, body.Name, StringComparison.Ordinal))
                continue;
            Vector3d offset = (blocker.Center - body.CameraPosition) / body.Radius;
            if (receiverAxes is { } axes)
                offset = Rotate(offset, axes);
            _archivedBlockers[count++] = new((float)offset.X, (float)offset.Y, (float)offset.Z, (float)(blocker.Radius / body.Radius));
            if (count == _archivedBlockers.Length)
                break;
        }
        material.SetShaderParameter("archived_shadow_count", count);
        if (count == 0)
            return;
        Vector3d sun = (scene.SunPosition - body.CameraPosition) / body.Radius;
        material.SetShaderParameter("archived_blockers", _archivedBlockers);
        if (receiverAxes is null)
            material.SetShaderParameter("archived_render_radius", renderRadius);
        else
            sun = Rotate(sun, receiverAxes.Value);
        material.SetShaderParameter("archived_sun", new Vector3((float)sun.X, (float)sun.Y, (float)sun.Z));
        material.SetShaderParameter("archived_sun_radius", (float)(scene.SunRadius / body.Radius));
        material.SetShaderParameter("archived_light_angle", (float)body.LightAngularRadius);
        material.SetShaderParameter("archived_second_angle", (float)body.SecondLightAngularRadius);

        static Vector3d Rotate(Vector3d vector, Basis axes)
        {
            var local = axes * new Vector3((float)vector.X, (float)vector.Y, (float)vector.Z);
            return new(local.X, local.Y, local.Z);
        }
    }

    private void DrawArchivedEarthLimb(MeshInstance3D surface, RecordedBodyGeometry body, RecordedSceneGeometry scene,
        Vector3 light)
    {
        double ground = body.Radius * scene.KilometersPerUnit;
        double height = (body.AtmosphereRadius - body.Radius) * scene.KilometersPerUnit;
        if (!float.IsFinite((float)ground) || !float.IsFinite((float)height) ||
            surface.Basis.Determinant() == 0)
            return;

        if (ArchivedEarthLimb is null)
        {
            ArchivedEarthLimb = new()
            {
                Mesh = _archivedSphere,
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_limb.gdshader") },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(ArchivedEarthLimb);
        }

        var material = (ShaderMaterial)ArchivedEarthLimb.MaterialOverride;
        material.SetShaderParameter("earth_radius_km", (float)ground);
        material.SetShaderParameter("shell_height_override_km", (float)height);
        material.SetShaderParameter("sun_direction", (surface.Basis.Orthonormalized().Inverse() * light).Normalized());
        BindArchivedShadows(material, scene, body, 1, ArchivedReceiverAxes(surface));
        material.SetShaderParameter("archived_polar_ratio", (float)body.PolarScale);
        ArchivedEarthLimb.Transform = new(surface.Basis.Scaled(Vector3.One * (float)(body.AtmosphereRadius / body.Radius)),
            surface.Position);
        ArchivedEarthLimb.Show();
    }

    private void DrawArchivedWorldAtmosphere(ObjectId id, MeshInstance3D surface, RecordedBodyGeometry body,
        RecordedSceneGeometry scene, Vector3 light)
    {
        bool venus = id == SolarSystemSample.VenusId;
        var lookup = _archivedAtmosphereLookups.GetValueOrDefault(id);
        if (!venus && lookup is null)
            return;

        float ground = (float)(body.Radius * scene.KilometersPerUnit);
        float height = (float)((body.AtmosphereRadius - body.Radius) * scene.KilometersPerUnit);
        float shellScale = (float)(body.AtmosphereRadius / body.Radius);
        if (!float.IsFinite(ground + height) || !float.IsFinite(shellScale) || surface.Basis.Determinant() == 0)
            return;

        if (!_archivedAtmospheres.TryGetValue(id, out var shell))
        {
            var limb = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/world_orbit_limb.gdshader") };
            limb.SetShaderParameter("venus_scattering", venus);
            lookup?.Bind(limb);
            Material shellMaterial = limb;
            if (venus)
            {
                limb.RenderPriority = -1;
                shellMaterial = new ShaderMaterial
                {
                    Shader = GD.Load<Shader>("res://Shaders/venus_orbit_transmission.gdshader"),
                    RenderPriority = -2,
                    NextPass = limb
                };
            }
            shell = new MeshInstance3D
            {
                Mesh = _archivedSphere, MaterialOverride = shellMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(shell);
            _archivedAtmospheres.Add(id, shell);
        }

        var material = (ShaderMaterial)shell.MaterialOverride;
        var scattering = material.NextPass as ShaderMaterial ?? material;
        material.SetShaderParameter("ground_radius_km", ground);
        material.SetShaderParameter("shell_height_km", height);
        scattering.SetShaderParameter("ground_radius_km", ground);
        scattering.SetShaderParameter("shell_height_km", height);
        scattering.SetShaderParameter("light_in_body", (surface.Basis.Orthonormalized().Inverse() * light).Normalized());
        BindArchivedShadows(scattering, scene, body, 1, ArchivedReceiverAxes(surface));
        scattering.SetShaderParameter("archived_polar_ratio", (float)body.PolarScale);
        shell.Transform = new(surface.Basis.Scaled(Vector3.One * shellScale), surface.Position);
        shell.Show();
    }

    private void DrawArchivedRings(ObjectId id, MeshInstance3D surface, RecordedBodyGeometry body,
        RecordedSceneGeometry scene, float radius, Vector3 light, Texture2D texture)
    {
        var radii = new Vector2((float)(body.RingInnerRadius / body.Radius), (float)(body.RingOuterRadius / body.Radius));
        if (surface.Basis.Determinant() == 0)
            return;
        if (!_archivedRings.TryGetValue(id, out var rings))
        {
            var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/planet_rings.gdshader") };
            material.SetShaderParameter("rings_map", texture);
            var node = new MeshInstance3D
            {
                MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(node);
            rings = (node, default);
        }
        var shader = (ShaderMaterial)rings.Node.MaterialOverride;
        if (rings.Radii != radii)
        {
            rings.Node.Mesh = PlanetRingRendering.RingMesh(radii.X, radii.Y, shader);
            _archivedRings[id] = (rings.Node, radii);
        }
        Basis rotation = surface.Basis.Orthonormalized();
        shader.SetShaderParameter("sun_in_body", rotation.Inverse() * (light == Vector3.Zero ? Vector3.Right : light));
        shader.SetShaderParameter("polar_ratio", (float)body.PolarScale);
        shader.SetShaderParameter("solar_radius_radians", (float)body.LightAngularRadius);
        BindArchivedShadows(shader, scene, body, 1, ArchivedReceiverAxes(surface));
        rings.Node.Transform = new(rotation.Scaled(Vector3.One * radius), surface.Position);
        rings.Node.Show();
    }

    private Basis ArchivedReceiverAxes(MeshInstance3D surface) =>
        surface.Basis.Orthonormalized().Inverse() * _navigationCamera.Basis;

    private void DrawArchivedPoint(RecordedBodyPoint record, Vector3 center, float radius, RecordedStellarView? sky)
    {
        if (_archivedPointsUsed == _archivedPointPool.Count)
            _archivedPointPool.Add(CreateBodyPoint());
        var node = _archivedPointPool[_archivedPointsUsed++];
        var material = (ShaderMaterial)node.MaterialOverride;
        material.SetShaderParameter("magnitude", (float)record.Magnitude);
        material.SetShaderParameter("point_color", new Vector3((float)record.Color.X, (float)record.Color.Y, (float)record.Color.Z));
        material.SetShaderParameter("fade", (float)record.Fade);
        material.SetShaderParameter("faintest", sky?.FaintestMagnitude ?? 7.8f);
        material.SetShaderParameter("brightness_scale", sky?.BrightnessScale ?? .19f);
        material.SetShaderParameter("saturation_point", sky?.SaturationPoint ?? 2.6f);
        Vector3 toCamera = _navigationCamera.Position - center;
        node.Position = center + toCamera.Normalized() * Math.Min(toCamera.Length() * .9f, 1.01f * radius);
        _archivedPoints.Add(record.Name, node);
        node.Show();
    }

    private void HideArchivedBodies()
    {
        for (int index = 0; index < _archivedUsed; index++)
            _archivedBodies[index].Hide();
        _archivedUsed = 0;
        for (int index = 0; index < _archivedPointsUsed; index++)
            _archivedPointPool[index].Hide();
        _archivedPointsUsed = 0;
        _archivedPoints.Clear();
        _archivedVisible.Clear();
        HideArchivedComets();
        _archivedOverlayRoot?.Hide();
        _archivedSymbolCanvas?.Hide();
        ArchivedEarthLimb?.Hide();
        foreach (var rings in _archivedRings.Values)
            rings.Node.Hide();
        foreach (var atmosphere in _archivedAtmospheres.Values)
            atmosphere.Hide();
    }
}
