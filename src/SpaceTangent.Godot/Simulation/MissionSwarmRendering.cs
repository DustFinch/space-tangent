// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedMissionSwarm(MissionSwarmStyle Style, ArrayMesh Mesh, Texture2D Data);

internal static class MissionSwarmRendering
{
    internal static PreparedMissionSwarm Publish(MissionSwarmStyle style,
        Dictionary<string, (ArrayMesh Mesh, Texture2D Data)> resources)
    {
        if (!resources.TryGetValue(style.SourcePath, out var shared))
        {
            var elements = style.Elements;
            const int width = 1024, maxHeight = 8192;
            int height = (int)((elements.Count * 3L + width - 1) / width);
            if (height > maxHeight)
                throw new InvalidDataException("The swarm is too large for the point data texture.");
            var pixels = new float[width * height * 4];
            var data = elements.Attributes;
            var points = new Vector3[elements.Count];
            var coordinates = new Vector2[elements.Count];
            for (int i = 0; i < elements.Count; i++)
            {
                data.Slice(i * MissionSwarm.FieldsPerObject, MissionSwarm.FieldsPerObject).CopyTo(pixels.AsSpan(i * 12));
                coordinates[i] = new(i, 0);
            }
            using var image = Image.CreateFromData(width, height, false, Image.Format.Rgbaf, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = points;
            arrays[(int)Mesh.ArrayType.TexUV] = coordinates;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
            float radius = elements.BoundingRadiusKilometers;
            mesh.CustomAabb = new(-Vector3.One * radius, Vector3.One * (2 * radius));
            shared = (mesh, ImageTexture.CreateFromImage(image));
            resources.Add(style.SourcePath, shared);
        }
        return new(style, shared.Mesh, shared.Data);
    }

    internal static void Draw(MeshInstance3D node, PreparedMissionSwarm swarm, JulianInstant epoch, Basis3d axes,
        Vector3 position, double scale, Camera3D camera, float height)
    {
        var style = swarm.Style;
        float depth = position.DistanceTo(camera.Position);
        float pixelAngle = 2 * MathF.Tan(camera.Fov * Mathf.Pi / 360) / Math.Max(1, height);
        float pixels = (float)(style.Elements.BoundingRadiusKilometers * scale) / Math.Max(1e-6f, depth * pixelAngle);
        float fade = style.FadeSize > 0 ? Mathf.Clamp((pixels / style.FadeSize - 1) / 3, 0, 1) : 1;
        if (!style.Visible || fade <= 0)
            return;

        if (node.Mesh != swarm.Mesh || node.MaterialOverride is not ShaderMaterial)
        {
            node.Mesh = swarm.Mesh;
            node.MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/mission_swarm.gdshader") };
        }
        var material = (ShaderMaterial)node.MaterialOverride;
        material.SetShaderParameter("source_data", swarm.Data);
        material.SetShaderParameter("swarm_clock", style.Elements.TimeAt(epoch.ToJulianDate()));
        material.SetShaderParameter("point_size", style.PointSize);
        material.SetShaderParameter("swarm_color", new Vector4((float)style.Color.X, (float)style.Color.Y,
            (float)style.Color.Z, style.Opacity * fade));
        node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        int sign = style.ReverseEquatorialAxes ? -1 : 1;
        node.Transform = new(new Basis(Axis(axes.X * sign), Axis(axes.Y * sign), Axis(axes.Z)), position);
        node.Show();

        Vector3 Axis(Vector3d value) => new((float)(value.X * scale), (float)(value.Z * scale), (float)(-value.Y * scale));
    }
}
