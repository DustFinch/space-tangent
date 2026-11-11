// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedSscBody(InstalledSscBody Body, ArrayMesh? Model, double ModelDiameter);

internal partial class SimulationViewport
{
    private IReadOnlyList<PreparedSscBody>? _sscAssets;
    private readonly Dictionary<ObjectId, (PreparedSscBody Asset, MeshInstance3D Node)> _sscNodes = new();

    internal void ReplaceSscBodies(IReadOnlyList<PreparedSscBody> assets)
    {
        if (ReferenceEquals(_sscAssets, assets))
            return;
        _sscAssets = assets;
        foreach (var (_, node) in _sscNodes.Values)
        {
            RemoveChild(node);
            node.QueueFree();
        }
        _sscNodes.Clear();
        foreach (PreparedSscBody asset in assets)
        {
            var color = asset.Body.Definition.Color;
            MeshInstance3D node;
            if (asset.Model is { } mesh)
            {
                node = new() { Mesh = mesh, Visible = false };
                AddChild(node);
            }
            else
                node = CreateNavigationSphere(new((float)color.X, (float)color.Y, (float)color.Z));
            _sscNodes.Add(asset.Body.Id, (asset, node));
        }
    }

    internal MeshInstance3D? ImportedBodyNode(ObjectId id) => _sscNodes.TryGetValue(id, out var entry) ? entry.Node : null;

    private void DrawSscBodies(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (var (asset, node) in _sscNodes.Values)
        {
            node.Hide();
            var body = asset.Body;
            if (!snapshot.TryState(body.Id, out CartesianState state) ||
                !snapshot.TryOrientation(body.Id, out FrameKinematics frame))
                continue;
            var (position, projection) = project(state.PositionKilometers);
            double radius = body.Definition.RadiusKilometers * projection / units;
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
                Basis3d axes = frame.ToWorld, rotation = body.Definition.MeshRotation;
                Vector3 Column(Vector3d value) => ToGodot(axes.Transform(rotation.Transform(value)) * scale, 1);
                Basis basis = new(Column(new(1, 0, 0)), Column(new(0, 1, 0)), Column(new(0, 0, 1)));
                Vector3 offset = Column(body.Definition.MeshCenter);
                node.Transform = new(basis, position - offset);
            }
            node.Show();
        }
    }

    private void HideSscBodies()
    {
        foreach (var (_, node) in _sscNodes.Values)
            node.Hide();
    }
}
