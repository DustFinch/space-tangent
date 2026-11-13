// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedImportedBody(ObjectId Id, CatalogueKind Kind, Vector3d Color, double RadiusKilometers,
    Basis3d MeshRotation, Vector3d MeshCenter, ArrayMesh? Model, double ModelDiameter,
    IReadOnlyList<PreparedMissionGeometry>? Geometry = null, PreparedMissionObservation? Observation = null)
{
    internal Vector3d? EllipsoidRadiiKilometers { get; init; }
    internal Texture2D? Surface { get; init; }
    internal Vector3d? MeshSpan { get; init; }
}

internal sealed record PreparedMissionGeometry(JulianInstant Start, JulianInstant End, double RadiusKilometers,
    Basis3d MeshRotation, Vector3d MeshOffset, ArrayMesh? Model, double ModelDiameter, MissionSensor? Sensor,
    IReadOnlyList<PreparedParticleEmitter>? Particles)
{
    internal MissionAxesStyle? Axes { get; init; }
    internal MissionVectorStyle? VectorOverlay { get; init; }
    internal Vector3d? EllipsoidRadiiKilometers { get; init; }
    internal StandardMaterial3D? GlobeMaterial { get; init; }
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
    private readonly SphereMesh _missionMarkerMesh = new() { Radius = 1, Height = 2, RadialSegments = 32, Rings = 16 };
    private readonly StandardMaterial3D _missionMarkerMaterial = new()
    {
        AlbedoColor = new(.8f, .8f, .8f),
        EmissionEnabled = true,
        Emission = new(.8f, .8f, .8f),
        EmissionEnergyMultiplier = .14f
    };
    private readonly Dictionary<ObjectId, (PreparedImportedBody Asset, MeshInstance3D Node)> _importedNodes = new();

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
        _missionObservations.Clear();
        foreach (var mesh in _missionVectorMeshes.Values)
            mesh.Dispose();
        _missionVectorMeshes.Clear();
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
            if (asset.Model is null && asset.Surface is { } map && node.MaterialOverride is StandardMaterial3D material)
            {
                material.AlbedoColor = Colors.White;
                material.AlbedoTexture = map;
            }
            if (asset.Observation is { } observation)
                _missionObservations.Add(asset.Id, new(node, observation));
            _importedNodes.Add(asset.Id, (asset, node));
        }
    }

    internal MeshInstance3D? ImportedBodyNode(ObjectId id) => _importedNodes.TryGetValue(id, out var entry) ? entry.Node : null;

    private void DrawImportedBodies(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _missionParticleRendering?.Hide();
        _missionSensorRendering?.Clear();
        foreach (var (asset, node) in _importedNodes.Values)
        {
            node.Hide();
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
                if (node.Mesh != mesh || node.MaterialOverride != material)
                {
                    node.Mesh = mesh;
                    node.MaterialOverride = material;
                }
            }
            double radius = (active?.RadiusKilometers ?? body.RadiusKilometers) * projection / units;
            if (radius < .035 && pose.Target != body.Id)
                continue;
            if ((active?.EllipsoidRadiiKilometers ?? ((active?.Model ?? asset.Model) is null ? asset.EllipsoidRadiiKilometers : null)) is { } radii)
            {
                double factor = projection / units;
                var scale = new Vector3((float)(radii.X * factor), (float)(radii.Z * factor), (float)(radii.Y * factor));
                node.Transform = new(PlanetBasis(frame.ToWorld) * Basis.FromScale(scale), position);
            }
            else if ((active?.Model ?? asset.Model) is null)
            {
                node.Position = position;
                node.Scale = Vector3.One * (float)radius;
            }
            else
            {
                double scale = radius * 2 / (active?.ModelDiameter ?? asset.ModelDiameter);
                Basis3d axes = frame.ToWorld, rotation = active?.MeshRotation ?? body.MeshRotation;
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
                Vector3 offset = active is null ? -Column(body.MeshCenter) :
                    ToGodot(axes.Transform(active.MeshOffset) * scale, 1);
                node.Transform = new(basis, position + offset);
            }
            node.Show();
        }
        _missionSensorRendering?.Finish();
    }

    private MeshInstance3D? _localSatelliteMarker;
    private ObjectId? _localSatelliteId;
    internal bool LocalSatelliteVisible(ObjectId id) => _localSatelliteId == id && _localSatelliteMarker is { Visible: true };

    private void DrawLocalSatellite(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _localSatelliteMarker?.Hide();
        _localSatelliteId = null;
        if (pose.Target is not { } id || _satellites.ContainsKey(id) ||
            CatalogueWorld?.FindLocalSatellite(id) is null || !snapshot.TryState(id, out var state))
            return;
        var (position, projection) = project(state.PositionKilometers);
        _localSatelliteMarker ??= CreateNavigationSphere(new(.8f, .8f, .8f));
        _localSatelliteMarker.Position = position;
        _localSatelliteMarker.Scale = Vector3.One * (float)(NavigationCamera.BodyRadiusKilometers(id) * projection / units);
        _localSatelliteId = id;
        _localSatelliteMarker.Show();
    }

    private void HideImportedBodies()
    {
        _missionParticleRendering?.Hide();
        _missionSensorRendering?.Clear();
        foreach (var (_, node) in _importedNodes.Values)
            node.Hide();
    }
}
