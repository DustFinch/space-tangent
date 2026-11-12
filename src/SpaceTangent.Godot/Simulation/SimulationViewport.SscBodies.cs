// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedImportedBody(ObjectId Id, CatalogueKind Kind, Vector3d Color, double RadiusKilometers,
    Basis3d MeshRotation, Vector3d MeshCenter, ArrayMesh? Model, double ModelDiameter);

internal partial class SimulationViewport
{
    private IReadOnlyList<PreparedImportedBody>? _importedAssets;
    private readonly Dictionary<ObjectId, (PreparedImportedBody Asset, MeshInstance3D Node)> _importedNodes = new();

    internal void ReplaceImportedBodies(IReadOnlyList<PreparedImportedBody> assets)
    {
        if (ReferenceEquals(_importedAssets, assets))
            return;
        _importedAssets = assets;
        foreach (var (_, node) in _importedNodes.Values)
        {
            RemoveChild(node);
            node.QueueFree();
        }
        _importedNodes.Clear();
        foreach (PreparedImportedBody asset in assets)
        {
            var color = asset.Color;
            MeshInstance3D node;
            if (asset.Model is { } mesh)
            {
                node = new() { Mesh = mesh, Visible = false };
                AddChild(node);
            }
            else
                node = CreateNavigationSphere(new((float)color.X, (float)color.Y, (float)color.Z));
            _importedNodes.Add(asset.Id, (asset, node));
        }
    }

    internal MeshInstance3D? ImportedBodyNode(ObjectId id) => _importedNodes.TryGetValue(id, out var entry) ? entry.Node : null;

    private void DrawImportedBodies(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (var (asset, node) in _importedNodes.Values)
        {
            node.Hide();
            var body = asset;
            if (!snapshot.TryState(body.Id, out CartesianState state) ||
                !snapshot.TryOrientation(body.Id, out FrameKinematics frame))
                continue;
            var (position, projection) = project(state.PositionKilometers);
            double radius = body.RadiusKilometers * projection / units;
            if (radius < .035 && pose.Target != body.Id)
                continue;
            if (asset.Model is null)
            {
                node.Position = position;
                node.Scale = Vector3.One * (float)radius;
            }
            else
            {
                double scale = radius * 2 / asset.ModelDiameter;
                Basis3d axes = frame.ToWorld, rotation = body.MeshRotation;
                Vector3 Column(Vector3d value) => ToGodot(axes.Transform(rotation.Transform(value)) * scale, 1);
                Basis basis = new(Column(new(1, 0, 0)), Column(new(0, 1, 0)), Column(new(0, 0, 1)));
                Vector3 offset = Column(body.MeshCenter);
                node.Transform = new(basis, position - offset);
            }
            node.Show();
        }
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
        foreach (var (_, node) in _importedNodes.Values)
            node.Hide();
    }
}
