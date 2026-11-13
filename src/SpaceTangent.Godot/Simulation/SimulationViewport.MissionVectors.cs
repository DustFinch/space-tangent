// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<MissionVectorStyle, ArrayMesh> _missionVectorMeshes = new();

    private void DrawMissionVector(MeshInstance3D node, MissionVectorStyle style, CartesianState state,
        WorldSnapshot snapshot, Basis3d frame, Vector3 position, double scale)
    {
        double length = style.SizeKilometers * scale;
        if (!style.Visible || style.Opacity <= 0 || length < .015 || length > float.MaxValue)
            return;

        Basis basis = PlanetBasis(frame);
        if (style.Target.HasValue)
        {
            if (!style.TryDirection(snapshot, state.PositionKilometers, out var direction))
                return;
            Vector3 along = ToGodot(direction, 1);
            Vector3 up = Math.Abs(along.Dot(Vector3.Up)) < .9f ? Vector3.Up : Vector3.Right;
            Vector3 cross = along.Cross(up).Normalized();
            basis = new(along, cross.Cross(along).Normalized(), cross);
        }
        if (!_missionVectorMeshes.TryGetValue(style, out var mesh))
            _missionVectorMeshes.Add(style, mesh = BuildMissionVector(style));
        node.Mesh = mesh;
        node.MaterialOverride = null;
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        node.Transform = new(basis.Scaled(Vector3.One * (float)length), position);
        node.Show();
    }

    private static ArrayMesh BuildMissionVector(MissionVectorStyle style)
    {
        var mesh = new ArrayMesh();
        if (style.Target.HasValue)
        {
            var tip = new Vector3(1, 0, 0);
            var neck = new Vector3(.90f, 0, 0);
            Add(Mesh.PrimitiveType.Lines,
                [Vector3.Zero, tip, tip, neck + Vector3.Up * .035f, tip, neck - Vector3.Up * .035f,
                    tip, neck + Vector3.Back * .035f, tip, neck - Vector3.Back * .035f], style.Opacity);
            return mesh;
        }
        Vector3 a = new(-1, 0, -1), b = new(1, 0, -1), c = new(1, 0, 1), d = new(-1, 0, 1);
        Add(Mesh.PrimitiveType.Triangles, [a, b, c, a, c, d], style.Opacity);
        var lines = new Vector3[4 * (style.Subdivisions + 1)];
        for (int i = 0; i <= style.Subdivisions; i++)
        {
            float offset = -1 + 2f * i / style.Subdivisions;
            lines[4 * i] = new(offset, 0, -1);
            lines[4 * i + 1] = new(offset, 0, 1);
            lines[4 * i + 2] = new(-1, 0, offset);
            lines[4 * i + 3] = new(1, 0, offset);
        }
        Add(Mesh.PrimitiveType.Lines, lines, Math.Min(1, 2 * style.Opacity));
        return mesh;

        void Add(Mesh.PrimitiveType primitive, Vector3[] vertices, double opacity)
        {
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            mesh.AddSurfaceFromArrays(primitive, arrays);
            var material = LineMaterial(style.Color, opacity);
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
    }
}
