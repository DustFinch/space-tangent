// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<(bool Visualizer, bool Natural, float Opacity), ArrayMesh> _missionAxes = new();

    private void DrawMissionAxes(MeshInstance3D node, MissionAxesStyle style, WorldSnapshot snapshot,
        Basis3d axes, Vector3 center, double scale)
    {
        double length = style.ScaleKilometers * scale;
        if (!style.Visible || length < .015)
            return;
        if (style.ReferenceOwner is { } owner &&
            snapshot.TryMissionReferenceFrame(owner, orbit: false, snapshot.Epoch, out _, out var reference))
            axes = reference.ToWorld;
        var key = (style.Kind != MissionAxesKind.Geometry, style.NaturalAxes, style.Opacity);
        if (!_missionAxes.TryGetValue(key, out var mesh))
            _missionAxes.Add(key, mesh = BuildMissionAxes(style));
        node.Mesh = mesh;
        node.MaterialOverride = null;
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        node.Transform = new(PlanetBasis(axes) * Basis.FromScale(Vector3.One * (float)length), center);
        node.Show();
    }

    private static ArrayMesh BuildMissionAxes(MissionAxesStyle style)
    {
        var mesh = new ArrayMesh();
        var arrow = MissionAxesMesh.Build(style.Kind);
        int[] indices = arrow.Triangles.ToArray();
        for (int axis = 0; axis < 3; axis++)
        {
            var vertices = new Vector3[arrow.Vertices.Count / 3];
            for (int index = 0; index < vertices.Length; index++)
            {
                float x = arrow.Vertices[3 * index], y = arrow.Vertices[3 * index + 1], z = arrow.Vertices[3 * index + 2];
                var direction = axis switch
                {
                    0 => new Vector3(z, y, -x),
                    1 => new Vector3(x, z, -y),
                    _ => new Vector3(x, y, z)
                };
                vertices[index] = new(style.NaturalAxes ? -direction.X : direction.X, direction.Z,
                    style.NaturalAxes ? direction.Y : -direction.Y);
            }
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Index] = indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(axis, new StandardMaterial3D
            {
                AlbedoColor = new(axis == 0 ? 1 : 0, axis == 1 ? 1 : 0, axis == 2 ? 1 : 0, style.Opacity),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = style.Opacity < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled
            });
        }
        return mesh;
    }
}
