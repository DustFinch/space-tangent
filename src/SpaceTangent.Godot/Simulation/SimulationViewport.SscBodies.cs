// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedSscRings(SscRings Geometry, Texture2D Texture);

internal sealed record PreparedImportedBody(ObjectId Id, CatalogueKind Kind, Vector3d Color, double RadiusKilometers,
    Basis3d MeshRotation, Vector3d MeshCenter, ArrayMesh? Model, double ModelDiameter,
    IReadOnlyList<PreparedMissionGeometry>? Geometry = null, PreparedMissionObservation? Observation = null)
{
    internal Vector3d? EllipsoidRadiiKilometers { get; init; }
    internal Texture2D? Surface { get; init; }
    internal ObjectId? StellarHost { get; init; }
    internal ObjectId? PhotometricParent { get; init; }
    internal double GeometricAlbedo { get; init; }
    internal double LabelOrbitRadius { get; init; }
    internal bool MinorMoon { get; init; }
    internal IReadOnlyList<StellarEntry>? PhotometricStars { get; init; }
    internal PreparedSscRings? Rings { get; init; }
    internal Vector3d? MeshSpan { get; init; }
    internal (Vector3d Center, Vector3d Span)? ModelBounds { get; init; }
}

internal sealed record PreparedMissionGeometry(JulianInstant Start, JulianInstant End, double RadiusKilometers,
    Basis3d MeshRotation, Vector3d MeshOffset, ArrayMesh? Model, double ModelDiameter, MissionSensor? Sensor,
    IReadOnlyList<PreparedParticleEmitter>? Particles)
{
    internal double? MeshScale { get; init; }
    internal MissionAxesStyle? Axes { get; init; }
    internal MissionVectorStyle? VectorOverlay { get; init; }
    internal PreparedMissionSwarm? Swarm { get; init; }
    internal Vector3d? EllipsoidRadiiKilometers { get; init; }
    internal StandardMaterial3D? GlobeMaterial { get; init; }
    internal ShaderMaterial? TiledGlobe { get; init; }
}

internal partial class SimulationViewport
{
    private MissionParticleRendering? _missionParticleRendering;
    internal MissionParticleRenderResult? MissionParticleResult(ObjectId id) => _missionParticleRendering?.Result(id);

    private MissionSensorRendering? _missionSensorRendering;
    internal MissionSensorRenderResult? MissionSensorResult(ObjectId id) => _missionSensorRendering?.Result(id);

    private readonly Dictionary<ObjectId, MissionObservationRendering> _missionObservations = new();
    internal int ObservationFootprintCount(ObjectId id) => _missionObservations.GetValueOrDefault(id)?.Count ?? 0;

    private IReadOnlyList<PreparedImportedBody>? _importedAssets;
    private readonly Dictionary<ShaderMaterial, ShaderMaterial> _missionTileMaterials = new();
    private readonly SphereMesh _missionMarkerMesh = new() { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 };
    private readonly StandardMaterial3D _missionMarkerMaterial = new()
    {
        AlbedoColor = new(.8f, .8f, .8f),
        EmissionEnabled = true,
        Emission = new(.8f, .8f, .8f),
        EmissionEnergyMultiplier = .14f
    };
    private readonly Dictionary<ObjectId, (PreparedImportedBody Asset, MeshInstance3D Node)> _importedNodes = new();

    private readonly Dictionary<ObjectId, MeshInstance3D> _importedRings = new();

    internal MeshInstance3D? ImportedRingNode(ObjectId id) => _importedRings.GetValueOrDefault(id);

    internal void ReplaceImportedBodies(IReadOnlyList<PreparedImportedBody> assets)
    {
        if (ReferenceEquals(_importedAssets, assets))
            return;
        _importedAssets = assets;
        _missionParticleRendering?.Clear();
        _missionSensorRendering?.Clear();
        foreach (var (_, node) in _importedNodes.Values)
        {
            RemoveChild(node);
            node.QueueFree();
        }
        _importedNodes.Clear();
        foreach (var ring in _importedRings.Values)
        {
            RemoveChild(ring);
            ring.QueueFree();
        }
        _importedRings.Clear();
        _missionObservations.Clear();
        foreach (var mesh in _missionVectorMeshes.Values)
            mesh.Dispose();
        _missionVectorMeshes.Clear();
        _missionDirectionLabels.Clear();
        foreach (var material in _missionTileMaterials.Values)
            material.Dispose();
        _missionTileMaterials.Clear();
        foreach (PreparedImportedBody asset in assets)
        {
            var color = asset.Color;
            MeshInstance3D node;
            if (asset.Geometry is not null)
            {
                node = new() { Visible = false };
                AddChild(node);
            }
            else if (asset.Model is { } mesh)
            {
                node = new() { Mesh = mesh, Visible = false };
                AddChild(node);
            }
            else
                node = CreateNavigationSphere(new((float)color.X, (float)color.Y, (float)color.Z));
            if (asset.StellarHost.HasValue && asset.Model is null && node.MaterialOverride is StandardMaterial3D)
            {
                _exoplanetGlobeShader ??= GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader");
                var globe = new ShaderMaterial { Shader = _exoplanetGlobeShader };
                globe.SetShaderParameter("globe_color", new Color((float)color.X, (float)color.Y, (float)color.Z));
                globe.SetShaderParameter("prime_meridian_offset", .25f);
                if (asset.Surface is { } surface)
                {
                    globe.SetShaderParameter("has_globe_map", true);
                    globe.SetShaderParameter("globe_map", surface);
                    globe.SetShaderParameter("globe_color", Colors.White);
                }
                node.MaterialOverride = globe;
            }
            else if (asset.Model is null && asset.Surface is { } map && node.MaterialOverride is StandardMaterial3D material)
            {
                material.AlbedoColor = Colors.White;
                material.AlbedoTexture = map;
            }
            if (asset.Observation is { } observation)
                _missionObservations.Add(asset.Id, new(node, observation));
            _importedNodes.Add(asset.Id, (asset, node));
            if (asset.Rings is { } rings)
            {
                var ringShader = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/planet_rings.gdshader") };
                ringShader.SetShaderParameter("rings_map", rings.Texture);
                ringShader.SetShaderParameter("polar_ratio", (float)((asset.EllipsoidRadiiKilometers?.Z ?? asset.RadiusKilometers)
                    / asset.RadiusKilometers));
                var ring = new MeshInstance3D
                {
                    Mesh = PlanetRingRendering.RingMesh((float)(rings.Geometry.InnerKilometers / asset.RadiusKilometers),
                        (float)(rings.Geometry.OuterKilometers / asset.RadiusKilometers), ringShader),
                    MaterialOverride = ringShader,
                    Visible = false,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                _importedRings.Add(asset.Id, ring);
                AddChild(ring);
            }
        }
    }

    internal MeshInstance3D? ImportedBodyNode(ObjectId id) => _importedNodes.TryGetValue(id, out var entry) ? entry.Node : null;

    private void DrawImportedBodies(WorldSnapshot snapshot, NavigationCamera pose, Vector3d eyeOffset, double units, SkyLayerSettings sky, ObjectId? selection,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _missionParticleRendering?.Hide();
        _missionSensorRendering?.Clear();
        foreach (var (asset, node) in _importedNodes.Values)
        {
            node.Hide();
            _importedRings.GetValueOrDefault(asset.Id)?.Hide();
            if (_missionDirectionLabels.TryGetValue(node, out var directionLabel))
                directionLabel.Hide();
            var body = asset;
            if (!snapshot.TryState(body.Id, out CartesianState state) ||
                !snapshot.TryOrientation(body.Id, out FrameKinematics frame))
                continue;
            var (position, projection) = project(state.PositionKilometers);
            if (asset.Observation is { } observation)
            {
                if (snapshot.TryOrientation(observation.Style.Target, out FrameKinematics targetFrame))
                {
                    float displayRadius = (float)(observation.RadiusKilometers * projection / units);
                    if (displayRadius >= .025f)
                        _missionObservations[asset.Id].Draw(snapshot.Epoch, new(
                            PlanetBasis(targetFrame.ToWorld).Scaled(Vector3.One * displayRadius), position));
                }
                continue;
            }
            PreparedMissionGeometry? active = null;
            if (asset.Geometry is { } intervals)
            {
                foreach (var span in intervals)
                {
                    if (snapshot.Epoch.CompareTo(span.Start) >= 0 && snapshot.Epoch.CompareTo(span.End) < 0)
                    {
                        active = span;
                        break;
                    }
                }
                if (active is null)
                    continue;
                if (active.Swarm is { } swarm)
                {
                    MissionSwarmRendering.Draw(node, swarm, snapshot.Epoch, frame.ToWorld, position, projection / units,
                        _navigationCamera, GetViewport().GetVisibleRect().Size.Y);
                    continue;
                }
                if (active.VectorOverlay is { } overlay)
                {
                    DrawMissionVector(node, overlay, state, snapshot, frame.ToWorld, position, projection / units);
                    continue;
                }
                if (active.Axes is { } axes)
                {
                    DrawMissionAxes(node, axes, snapshot, frame.ToWorld, position, projection / units);
                    continue;
                }
                if (active.Sensor is { } sensor)
                {
                    _missionSensorRendering ??= new(this);
                    _missionSensorRendering.Draw(asset.Id, sensor, state.PositionKilometers, frame.ToWorld, snapshot, units, project);
                    continue;
                }
                if (active.Particles is { } particles)
                {
                    _missionParticleRendering ??= new(this, _navigationCamera);
                    _missionParticleRendering.Draw(body.Id, particles, snapshot.Epoch, position, frame.ToWorld, projection / units);
                    continue;
                }
                Mesh mesh = active.Model is { } model ? model : _missionMarkerMesh;
                Material? material = active.Model is null ? active.GlobeMaterial ?? _missionMarkerMaterial : null;
                if (active.TiledGlobe is { } tiled)
                {
                    if (!_missionTileMaterials.TryGetValue(tiled, out var local))
                        _missionTileMaterials.Add(tiled, local = (ShaderMaterial)tiled.Duplicate());
                    if (snapshot.SolarSystem is { } solar)
                    {
                        Vector3d light = solar.Sun.PositionKilometers - state.PositionKilometers;
                        if (light.Length > 0)
                            local.SetShaderParameter("light_in_body", ToGodot(frame.ToWorld.Transpose().Transform(light), light.Length));
                    }
                    material = local;
                }
                if (node.Mesh != mesh || node.MaterialOverride != material)
                {
                    node.Mesh = mesh;
                    node.MaterialOverride = material;
                }
            }
            double physicalRadius = active?.RadiusKilometers ?? body.RadiusKilometers;
            double radius = physicalRadius * projection / units;
            Vector3d towardEye = (pose.AnchorKilometers - state.PositionKilometers) + eyeOffset;
            if (sky.Labels && CatalogueWorld is { } labelWorld && labelWorld.Catalogue.Find(body.Id) is { } catalogue)
            {
                bool minor = body.Kind is CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.Spacecraft || body.MinorMoon;
                if (!minor || sky.MinorBodyLabels || selection == body.Id)
                {
                    ObjectId? primary = body.PhotometricParent ?? catalogue.Parent;
                    if (primary is null && snapshot.TryStateCenter(body.Id, out var center))
                        primary = center;
                    DrawBodyLabel(body.Id, catalogue.Name, primary, minor ? 0 : body.LabelOrbitRadius,
                        physicalRadius, snapshot, state.PositionKilometers, towardEye, position, (float)radius);
                }
            }
            ObjectId? photometricParent = body.PhotometricParent;
            if (photometricParent is null && active is not null && snapshot.TryStateCenter(body.Id, out var centerId))
                photometricParent = centerId;
            if (photometricParent is { } parent)
            {
                var tint = new Color((float)body.Color.X, (float)body.Color.Y, (float)body.Color.Z);
                double albedo = active is null ? body.GeometricAlbedo : .5;
                if (!DrawReflectedBody(body.Id, parent, physicalRadius, albedo, tint,
                    snapshot, towardEye, state.PositionKilometers, position, (float)radius, body.PhotometricStars))
                    continue;
            }
            else if (radius < .035 && pose.Target != body.Id)
                continue;
            if ((active?.EllipsoidRadiiKilometers ?? ((active?.Model ?? asset.Model) is null ? asset.EllipsoidRadiiKilometers : null)) is { } radii)
            {
                double factor = projection / units;
                var scale = new Vector3((float)(radii.X * factor), (float)(radii.Z * factor), (float)(radii.Y * factor));
                node.Transform = new(PlanetBasis(frame.ToWorld) * Basis.FromScale(scale), position);
            }
            else if (asset.StellarHost is { } host && asset.Model is null && node.MaterialOverride is ShaderMaterial globe &&
                snapshot.TryState(host, out var stellarState))
            {
                Basis basis = PlanetBasis(frame.ToWorld);
                Vector3d illumination = (stellarState - state).PositionKilometers;
                if (illumination.Length > 0)
                    globe.SetShaderParameter("light_in_body", basis.Inverse() * ToGodot(illumination, illumination.Length));
                node.Transform = new(basis * Basis.FromScale(Vector3.One * (float)radius), position);
            }
            else if ((active?.Model ?? asset.Model) is null)
            {
                node.Position = position;
                node.Scale = Vector3.One * (float)radius;
            }
            else
            {
                double scale = active?.MeshScale is { } physicalScale ? physicalScale * projection / units :
                    radius * 2 / (active?.ModelDiameter ?? asset.ModelDiameter);
                Basis3d axes = frame.ToWorld, rotation = active?.MeshRotation ?? body.MeshRotation;
                Vector3d modelCenter = body.MeshCenter;
                if (active is null && asset.ModelBounds is { } bounds)
                {
                    // Catalogue spacecraft are centered on their bounds and sized by the longest extent.
                    double extent = Math.Max(bounds.Span.X, Math.Max(bounds.Span.Y, bounds.Span.Z));
                    scale = radius * 2 / extent;
                    modelCenter += bounds.Center;
                }
                Vector3d modelScale = new(scale, scale, scale);
                if (active is null && asset.EllipsoidRadiiKilometers is { } shape &&
                    asset.MeshSpan is { X: > 1e-12, Y: > 1e-12, Z: > 1e-12 } span)
                {
                    double factor = 2 * projection / units;
                    modelScale = new(shape.X * factor / span.X, shape.Z * factor / span.Y, shape.Y * factor / span.Z);
                }
                Vector3 Column(Vector3d value) => ToGodot(axes.Transform(rotation.Transform(new(
                    value.X * modelScale.X, value.Y * modelScale.Y, value.Z * modelScale.Z))), 1);
                Basis basis = new(Column(new(1, 0, 0)), Column(new(0, 1, 0)), Column(new(0, 0, 1)));
                Vector3 offset = active is null ? -Column(modelCenter) :
                    ToGodot(axes.Transform(active.MeshOffset) * scale, 1);
                node.Transform = new(basis, position + offset);
            }
            node.Show();
            if (_importedRings.TryGetValue(asset.Id, out var ring))
            {
                ObjectId lightId = asset.StellarHost ?? SolarSystemSample.SunId;
                if (!snapshot.TryState(lightId, out var light))
                    continue;
                Vector3d towardSun = light.PositionKilometers - state.PositionKilometers;
                double lightRadius = asset.StellarHost is not null && CatalogueWorld is { } world
                    ? Math.Max(0, world.FindStellarTarget(lightId)?.RadiusKilometers ?? 0)
                    : SpaceTangent.Core.Observing.SolarOccultation.SunRadiusKilometers;
                if (towardSun.Length > 0)
                {
                    Basis basis = PlanetBasis(frame.ToWorld);
                    var ringMaterial = (ShaderMaterial)ring.MaterialOverride;
                    ringMaterial.SetShaderParameter("sun_in_body", basis.Inverse() * ToGodot(towardSun, towardSun.Length));
                    ringMaterial.SetShaderParameter("solar_radius_radians", (float)Math.Asin(Math.Min(1, lightRadius / towardSun.Length)));
                    ring.Transform = new(basis * Basis.FromScale(Vector3.One * (float)radius), position);
                    ring.Show();
                }
            }
        }
        _missionSensorRendering?.Finish();
    }

    private IReadOnlyDictionary<ObjectId, double> _satelliteMagnitudes = new Dictionary<ObjectId, double>();
    private readonly Dictionary<ObjectId, MeshInstance3D> _localSatelliteMarkers = new();
    private SphereMesh? _localSatelliteSphere;
    internal bool LocalSatelliteVisible(ObjectId id) => _localSatelliteMarkers.GetValueOrDefault(id)?.Visible == true;

    private void HideLocalSatelliteMarkers()
    {
        foreach (var marker in _localSatelliteMarkers.Values)
            marker.Hide();
    }

    private void DrawLocalSatellites(WorldSnapshot snapshot, NavigationCamera pose, Vector3d eyeOffset, double units, SkyLayerSettings sky, ObjectId? selection,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideLocalSatelliteMarkers();
        if (CatalogueWorld is not { } world)
            return;
        if (sky.HideSatellites && sky.Starlink != true)
        {
            if (selection is { } selected && world.FindLocalSatellite(selected) is { } selectedSatellite)
                DrawSatellite(selectedSatellite, true, []);
            if (pose.Target is { } tracked && tracked != selection && world.FindLocalSatellite(tracked) is { } trackedSatellite)
                DrawSatellite(trackedSatellite, true, []);
            return;
        }

        var frustum = _navigationCamera.GetFrustum().ToArray();
        foreach (var satellite in world.LocalSatellites)
        {
            bool selected = satellite.Id == selection || satellite.Id == pose.Target;
            bool starlink = StarlinkPhotometry.IsMember(satellite);
            bool visible = starlink ? sky.Starlink ?? !sky.HideSatellites : !sky.HideSatellites;
            if (selected || visible)
                DrawSatellite(satellite, selected, frustum);
        }

        void DrawSatellite(LocalTleSatellite satellite, bool selected, Plane[] planes)
        {
            var id = satellite.Id;
            if (_satellites.ContainsKey(id) || !snapshot.TryState(id, out var state))
                return;

            var (position, projection) = project(state.PositionKilometers);
            if (!selected)
            {
                foreach (var plane in planes)
                    if (plane.DistanceTo(position) > 0)
                        return;
            }
            var color = new Color(.8f, .8f, .8f);
            const double tleRadius = .005, tleAlbedo = .3;
            float projectedRadius = (float)(tleRadius * projection / units);
            Vector3d towardEye = (pose.AnchorKilometers - state.PositionKilometers) + eyeOffset;
            bool resolved = _satelliteMagnitudes.ContainsKey(id)
                ? DrawSatellitePoint(id, snapshot, state.PositionKilometers, towardEye, tleRadius, position, projectedRadius)
                : DrawReflectedBody(id, SolarSystemSample.EarthId, tleRadius, tleAlbedo, color,
                    snapshot, towardEye, state.PositionKilometers, position, projectedRadius);
            if (sky.Labels && (id == selection || (sky.MinorBodyLabels && (resolved || BodyPoint(id)?.Visible == true))))
                DrawBodyLabel(id, satellite.Name, SolarSystemSample.EarthId, 0, tleRadius,
                    snapshot, state.PositionKilometers, towardEye, position, projectedRadius);
            if (!resolved)
                return;
            if (!_localSatelliteMarkers.TryGetValue(id, out var marker))
            {
                _localSatelliteSphere ??= new SphereMesh { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 };
                _localSatelliteMarkers.Add(id, marker = CreateNavigationSphere(color, sharedMesh: _localSatelliteSphere));
            }
            marker.Position = position;
            marker.Scale = Vector3.One * projectedRadius;
            marker.Show();
        }
    }

    private bool DrawSatellitePoint(ObjectId id, WorldSnapshot snapshot, Vector3d center, Vector3d towardEye,
        double radius, Vector3 position, float projectedRadius)
    {
        if (!_satelliteMagnitudes.TryGetValue(id, out double standard) || snapshot.SolarSystem is not { } solar)
            return true;
        double distance = towardEye.Length;
        double disc = ProjectedBodyPixels(radius, distance - radius);
        if (disc >= PointBlendPixels)
            return true;
        Vector3d sunward = solar.Sun.PositionKilometers - center;
        if (sunward.Length <= 0)
            return disc >= PointPixels;
        sunward /= sunward.Length;
        Vector3d fromEarth = center - solar.Earth.PositionKilometers;
        double along = Vector3d.Dot(fromEarth, sunward);
        bool eclipsed = along < 0 && (fromEarth - sunward * along).Length < EarthSurfaceLighting.EarthRadiusKilometers;
        if (!eclipsed)
        {
            double cosine = Vector3d.Dot(towardEye / distance, sunward);
            double illuminated = Math.Max(1e-6, (1 + cosine) / 2);
            double magnitude = standard + 5 * Math.Log10(distance / 1000) - 2.5 * Math.Log10(2 * illuminated);
            ShowBodyPoint(id, new(.8f, .8f, .8f), position, projectedRadius, disc, magnitude);
        }
        return disc >= PointPixels;
    }

    private void HideImportedBodies()
    {
        _missionParticleRendering?.Hide();
        _missionSensorRendering?.Clear();
        foreach (var (_, node) in _importedNodes.Values)
            node.Hide();
        foreach (var ring in _importedRings.Values)
            ring.Hide();
        foreach (var label in _missionDirectionLabels.Values)
            label.Hide();
    }
}
