// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<ObjectId, (ArrayMesh Mesh, double Scale)> _extrasolarOrbitMeshes = new();
    private readonly List<MeshInstance3D> _extrasolarOrbitNodes = [];
    private SimulationWorld? _extrasolarOrbitWorld;

    internal int VisibleExtrasolarOrbits => _extrasolarOrbitNodes.Count(node => node.Visible);

    private void DrawExtrasolarOrbits(NavigationCamera camera, WorldSnapshot snapshot, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideExtrasolarOrbits();
        if (!ShowPlanetOrbits || CatalogueWorld is not { } world || camera.Target is not { } target)
            return;
        if (!ReferenceEquals(_extrasolarOrbitWorld, world))
        {
            _extrasolarOrbitWorld = world;
            _extrasolarOrbitMeshes.Clear();
            foreach (var node in _extrasolarOrbitNodes)
                node.Mesh = null;
        }

        var host = world.SscBodies.FirstOrDefault(body => body.Id == target)?.StellarHost ??
            world.Exoplanets.Find(target)?.Host ?? target;
        int index = 0;
        foreach (var body in world.SscBodies)
        {
            if (body.StellarHost != host || body.Orbit is not { } orbit ||
                !snapshot.TryState(body.Id, out _) || !snapshot.TryState(orbit.Center, out var center))
                continue;
            var (position, projection) = project(center.PositionKilometers);
            if (!_extrasolarOrbitMeshes.TryGetValue(body.Id, out var geometry))
            {
                geometry = MakeExtrasolarOrbit(orbit.Points, body.Definition.Color);
                _extrasolarOrbitMeshes.Add(body.Id, geometry);
            }
            float scale = (float)(geometry.Scale * projection / units);
            if (scale < .12f || !float.IsFinite(scale))
                continue;
            if (index == _extrasolarOrbitNodes.Count)
            {
                var created = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                AddChild(created);
                _extrasolarOrbitNodes.Add(created);
            }
            var node = _extrasolarOrbitNodes[index++];
            node.Mesh = geometry.Mesh;
            node.Transform = new(Basis.FromScale(Vector3.One * scale), position);
            node.Show();
            if (index == 16)
                break;
        }
    }

    private static (ArrayMesh Mesh, double Scale) MakeExtrasolarOrbit(IReadOnlyList<Vector3d> points, Vector3d color)
    {
        double scale = points.Max(point => point.Length);
        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.Select(point => ToGodot(point, scale)).ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.LineStrip, arrays);
        mesh.SurfaceSetMaterial(0, LineMaterial(new((float)color.X, (float)color.Y, (float)color.Z), .55f));
        return (mesh, scale);
    }

    private void HideExtrasolarOrbits()
    {
        foreach (var node in _extrasolarOrbitNodes)
            node.Hide();
    }
}
