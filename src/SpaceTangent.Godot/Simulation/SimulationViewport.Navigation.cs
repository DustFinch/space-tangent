// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const float NavigationCameraUnits = 48;
    private const float NavigationFar = 12000;
    private Camera3D _navigationCamera = null!;
    private DirectionalLight3D _navigationLight = null!;
    private double _navigationUnits;
    private Vector3d _navigationEyeOffset;
    private MeshInstance3D _navigationSun = null!, _navigationEarth = null!, _navigationMoon = null!;

    private EarthOrbitRenderer? _earthOrbit;
    private ShaderMaterial _navigationMoonMaterial = null!;

    internal void SetEarthOrbitTextures(EarthOrbitTextures textures, OrbitAtmosphereTextures? lookup)
    {
        _earthOrbit = new(_navigationEarth, textures, lookup);
        _archivedEarthMaps = textures;
    }

    internal bool EarthOrbitAtmosphereActive => _earthOrbit?.Active == true;
    internal bool NavigationCameraCurrent => _navigationCamera.Current;
    internal Camera3D NavigationCameraNode => _navigationCamera;

    private void InitializeNavigation()
    {
        _navigationCamera = new Camera3D { Fov = 49, Near = .02f, Far = NavigationFar, KeepAspect = Camera3D.KeepAspectEnum.Height };
        AddChild(_navigationCamera);
        _navigationLight = new DirectionalLight3D { LightEnergy = 1.05f, Visible = false };
        AddChild(_navigationLight);
        _navigationSun = CreateNavigationSphere(new Color("ffd18d"), emissive: true);
        _navigationEarth = CreateNavigationSphere(new Color("4a76ad"));
        _navigationMoon = CreateNavigationSphere(new Color("c9c4b6"));
        _navigationMoonMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/moon_orbit.gdshader") };
        _navigationMoonMaterial.SetShaderParameter("earth_radius_km", (float)EarthSurfaceLighting.EarthRadiusKilometers);
        _navigationMoonMaterial.SetShaderParameter("moon_radius_km", (float)EarthSurfaceLighting.MoonRadiusKilometers);
        _navigationMoon.MaterialOverride = _navigationMoonMaterial;
        InitializeStellarRendering();
    }

    private MeshInstance3D CreateNavigationSphere(Color color, bool emissive = false)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 64, Rings = 32 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = emissive ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                EmissionEnabled = !emissive, Emission = color, EmissionEnergyMultiplier = .14f
            },
            Visible = false
        };
        AddChild(mesh);
        return mesh;
    }

    private void DrawNavigation(NavigationCamera pose, WorldSnapshot snapshot, SolarSystemSample solar, DeltaTModel deltaT,
        SkyLayerSettings sky, ObjectId? selection, double fieldOfView, bool inverted)
    {
        double units = pose.RangeKilometers / NavigationCameraUnits;
        var placement = pose.ResolvePlacement(snapshot, deltaT);
        _navigationEyeOffset = default;
        if (pose.Target is { } id && DemoWorlds.Find(id) is { } demo &&
            DemoWorlds.CreateSurface(demo) is { HasSurface: true } surface &&
            snapshot.TryState(id, out var demoState) && snapshot.TryOrientation(id, out var demoRotation))
        {
            Vector3d local = demoRotation.ToWorld.Transpose().Transform(
                (pose.AnchorKilometers - demoState.PositionKilometers) + placement.OffsetKilometers);
            if (local.Length > 0 && local.Length < demo.RadiusKilometers * 1.12)
            {
                var terrain = new ProceduralTerrain(surface, demo.PolarRadiusKilometers);
                var native = new Vector3d(local.X, local.Z, -local.Y) / local.Length;
                double clearance = local.Length - terrain.GroundRadiusKilometers(native);
                units = Math.Min(units, Math.Max(.05, clearance) / NavigationCameraUnits);
                _navigationEyeOffset = placement.OffsetKilometers;
            }
        }
        _navigationUnits = units;
        double azimuth = pose.AzimuthRadians, elevation = pose.ElevationRadians;
        var offset = new Vector3(
            (float)(Math.Cos(elevation) * Math.Sin(azimuth)),
            (float)Math.Sin(elevation),
            (float)(Math.Cos(elevation) * Math.Cos(azimuth)));
        _navigationCamera.Fov = (float)fieldOfView;
        _navigationCamera.Near = .02f;
        if (pose.Placement is not null)
        {
            _navigationCamera.Position = ToGodot(placement.OffsetKilometers - _navigationEyeOffset, units);
            _navigationCamera.LookAt(_navigationCamera.Position + ToGodot(placement.Forward, 1), ToGodot(placement.Up, 1));
        }
        else
        {
            _navigationCamera.Position = _navigationEyeOffset == default ? offset * NavigationCameraUnits : Vector3.Zero;
            _navigationCamera.LookAt(_navigationCamera.Position + ToGodot(placement.Forward, 1), ToGodot(placement.Up, 1));
        }
        if (inverted)
            _navigationCamera.RotateObjectLocal(Vector3.Back, Mathf.Pi);
        if (!_navigationCamera.Current)
            _navigationCamera.MakeCurrent();

        Vector3 light = ToGodot(solar.Sun.PositionKilometers - pose.AnchorKilometers, units);
        if (light.LengthSquared() > 1e-6f)
        {
            light = light.Normalized();
            _navigationLight.LookAt(-light, Math.Abs(light.Y) > .98f ? Vector3.Forward : Vector3.Up);
        }
        _navigationLight.Show();
        HidePlanetGlobes();
        PlaceBody(_navigationSun, solar.Sun.PositionKilometers, SolarSystemSample.SunId);
        PlaceBody(_navigationEarth, solar.Earth.PositionKilometers, SolarSystemSample.EarthId,
            SolarSystemSample.SunId, PlanetAlbedo(399), "6fa8ff");
        if (_earthOrbit is not null)
        {
            float projectedDistance = _navigationEarth.Position.DistanceTo(_navigationCamera.Position);
            _earthOrbit.Update(solar, deltaT, _navigationEarth.Visible && _navigationEarth.Scale.X > projectedDistance * .004f);
        }
        PlaceBody(_navigationMoon, solar.Moon.PositionKilometers, SolarSystemSample.MoonId,
            SolarSystemSample.EarthId, .136, "c9c4b6");
        _navigationMoonMaterial.SetShaderParameter("sun_from_moon", ToGodot((solar.Sun - solar.Moon).PositionKilometers, 1));
        _navigationMoonMaterial.SetShaderParameter("earth_from_moon", ToGodot((solar.Earth - solar.Moon).PositionKilometers, 1));
        DrawPlanetGlobes(snapshot, pose, placement.OffsetKilometers, units, sky, selection, ProjectWithScale);
        DrawWorldAtmospheres(snapshot, solar.Sun.PositionKilometers);
        DrawStellarObjects(pose, snapshot, sky, placement.OffsetKilometers, units, ProjectWithScale);
        DrawExtrasolarOrbits(pose, snapshot, units, ProjectWithScale);
        DrawCompactObjects(snapshot, sky, units, ProjectWithScale);
        DrawImportedBodies(snapshot, pose, placement.OffsetKilometers, units, ProjectWithScale);
        DrawLocalSatellite(snapshot, pose, placement.OffsetKilometers, units, ProjectWithScale);
        HideComets();
        DrawComets(snapshot, solar.Sun, units, ProjectWithScale);

        (Vector3 Position, double Scale) ProjectWithScale(Vector3d position)
        {
            Vector3 projected = Project(position, out double scale);
            return (projected, scale);
        }

        foreach (var (asset, node) in _satellites.Values)
        {
            if (!snapshot.TryState(asset.Track.Id, out var state) || !snapshot.TryOrientation(asset.Track.Id, out var orientation))
                continue;
            double radius = NavigationCamera.BodyRadiusKilometers(asset.Track.Id);
            var satelliteId = asset.Track.Id;
            if (!_satelliteMagnitudes.ContainsKey(satelliteId) && radius / pose.RangeKilometers < .0002 && pose.Target != satelliteId)
                continue;
            Vector3 position = Project(state.PositionKilometers, out double scale);
            float projectedRadius = (float)(radius / units * scale);
            Vector3d towardEye = (pose.AnchorKilometers - state.PositionKilometers) + placement.OffsetKilometers;
            if (!DrawSatellitePoint(satelliteId, snapshot, state.PositionKilometers, towardEye, radius, position, projectedRadius))
                continue;
            float modelScale = projectedRadius * asset.MarkerScale / 9;
            if (modelScale < 1e-7f)
                continue;
            Basis3d body = orientation.ToWorld, mesh = asset.Track.MeshToOrientedBody;
            Vector3 Column(Vector3d axis) => ToGodot(body.Transform(axis), 1) * modelScale;
            node.Transform = new(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)), position);
            node.Show();
        }

        DrawArchivedBodies(units);

        void PlaceBody(MeshInstance3D node, Vector3d position, ObjectId id,
            ObjectId? parentId = null, double albedo = 0, string tint = "")
        {
            node.Position = Project(position, out double scale);
            double physicalRadius = NavigationCamera.BodyRadiusKilometers(id);
            float radius = (float)(physicalRadius * scale / units);
            node.Scale = Vector3.One * radius;
            Vector3d towardEye = (pose.AnchorKilometers - position) + placement.OffsetKilometers;
            node.Visible = parentId is not { } parent ||
                DrawReflectedBody(id, parent, physicalRadius, albedo, new Color(tint), snapshot, towardEye, position, node.Position, radius);
            if (sky.Labels)
            {
                string name = PlanetCatalogue.Find(id)?.Name ?? (id == SolarSystemSample.SunId ? "Sun" : "Moon");
                double orbitRadius = id == SolarSystemSample.MoonId ? 384399 * 1.0549 :
                    id == SolarSystemSample.EarthId ? PlanetLabelOrbitRadius(399) : 0;
                DrawBodyLabel(id, name, parentId, orbitRadius, physicalRadius, snapshot, position, towardEye, node.Position, radius);
            }
        }

        Vector3 Project(Vector3d position, out double scale) => ProjectNavigation((position - pose.AnchorKilometers) - _navigationEyeOffset, units, out scale);
    }

    private static Vector3 ProjectNavigation(Vector3d relative, double units, out double scale)
    {
        double distance = relative.Length / units;
        // Fit distant bodies in the camera range while retaining their angular size.
        scale = distance > NavigationFar * .8 ? NavigationFar * .8 / distance : 1;
        return ToGodot(relative * scale, units);
    }

    internal Vector2? ProjectNavigationPoint(NavigationCamera pose, Vector3d position)
    {
        Vector3 point = ProjectNavigation((position - pose.AnchorKilometers) - _navigationEyeOffset, _navigationUnits, out _);
        return _navigationCamera.IsPositionBehind(point) ? null : _navigationCamera.UnprojectPosition(point);
    }

    private ObjectId? PickNavigationBody(Vector2 point)
    {
        if (!_navigationCamera.Current)
            return null;

        Vector3 origin = _navigationCamera.ProjectRayOrigin(point);
        Vector3 direction = _navigationCamera.ProjectRayNormal(point);
        ObjectId? result = null;
        float nearest = _navigationCamera.Far;
        Consider(SolarSystemSample.SunId, _navigationSun);
        Consider(SolarSystemSample.EarthId, _navigationEarth);
        Consider(SolarSystemSample.MoonId, _navigationMoon);
        foreach (var (id, globe) in _planetGlobes)
            Consider(id, globe);
        foreach (var (id, satellite) in _satellites)
            Consider(id, satellite.Node);
        if (_localSatelliteId is { } tracked && _localSatelliteMarker is { } marker)
            Consider(tracked, marker);
        foreach (var (id, body) in _importedNodes)
            if (body.Asset.Observation is null && body.Asset.Geometry?.Any(span => span.VectorOverlay is not null || span.Swarm is not null) != true)
                Consider(id, body.Node);
        foreach (var (id, disk) in _visibleStars)
            Consider(id, disk);
        foreach (var (id, demo) in _demoGlobes)
            Consider(id, demo.Globe);
        foreach (var (id, planet) in _visibleExoplanets)
            Consider(id, planet);
        foreach (var (id, deepSky) in _deepSkyObjects)
            Consider(id, deepSky);
        if (_compactCompanion is { } companion)
            Consider(SimulationWorld.CygnusCompanionId, companion);
        float closestPointPixelsSquared = 25;
        float pointDepth = nearest;
        foreach (var (id, sprite) in _bodyPoints)
        {
            if (!sprite.Visible || _navigationCamera.IsPositionBehind(sprite.Position))
                continue;
            float depth = (sprite.Position - origin).Dot(direction);
            float pixelsSquared = _navigationCamera.UnprojectPosition(sprite.Position).DistanceSquaredTo(point);
            if (depth < _navigationCamera.Near || depth >= nearest || pixelsSquared > closestPointPixelsSquared ||
                (pixelsSquared == closestPointPixelsSquared && depth >= pointDepth))
                continue;
            closestPointPixelsSquared = pixelsSquared;
            pointDepth = depth;
            result = id;
        }
        return result ?? PickCompactObject(point) ?? PickDistantStar(point);

        void Consider(ObjectId id, MeshInstance3D node)
        {
            if (!node.Visible || node.Mesh is null)
                return;

            // A globe is a unit sphere in local coordinates. Models use a bounding sphere.
            Aabb bounds = node.GetAabb();
            float radius = node.Mesh is SphereMesh ? 1 : bounds.Size.Length() / 2;
            if (radius <= 0)
                return;
            Transform3D inverse = node.Transform.AffineInverse();
            Vector3 eye = inverse * origin - bounds.GetCenter();
            Vector3 ray = inverse.Basis * direction;
            float length = ray.Length();
            ray /= length;
            float along = -eye.Dot(ray);
            float miss = (eye + ray * along).LengthSquared();
            if (miss > radius * radius)
                return;
            float hit = (along - MathF.Sqrt(radius * radius - miss)) / length;
            if (hit >= _navigationCamera.Near && hit < nearest)
            {
                nearest = hit;
                result = id;
            }
        }
    }

    private void HideNavigation()
    {
        HideArchivedBodies();
        _navigationLight.Hide();
        _navigationSun.Hide();
        _navigationEarth.Hide();
        _navigationMoon.Hide();
        HidePlanetGlobes();
        HideStellarObjects();
        HideCompactObjects();
        HideExtrasolarOrbits();
        HideImportedBodies();
        _localSatelliteMarker?.Hide();
        HideComets();
    }
}
