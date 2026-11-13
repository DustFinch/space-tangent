// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MeshInstance3D? _archivedStarBackground, _archivedMovingStars;
    private RecordedStarField? _archivedBackgroundSource, _archivedMovingSource;
    private ReadOnlyMemory<byte> _archivedMovingPositions;
    internal int ArchivedStarCount => _archivedStarBackground?.Visible == true
        ? (_archivedBackgroundSource?.Count ?? 0) + (_archivedMovingSource?.Count ?? 0) : 0;

    private MeshInstance3D? _archivedStarPoints;
    private IReadOnlyList<RecordedStarPoint>? _archivedStarPointSource;

    private void DrawArchivedStarPoints(IReadOnlyList<RecordedStarPoint>? points)
    {
        if (points is not { Count: > 0 } || DisplayServer.GetName() == "headless")
        {
            if (_archivedStarPoints is { } hidden)
            {
                hidden.Hide();
                hidden.Mesh = null;
            }
            _archivedStarPointSource = null;
            return;
        }

        var node = _archivedStarPoints ??= CreateArchivedStarNode("ArchivedStarPoints", "archived_star_points");
        if (!ReferenceEquals(points, _archivedStarPointSource))
        {
            using var mesh = new SurfaceTool();
            mesh.Begin(Mesh.PrimitiveType.Triangles);
            ReadOnlySpan<Vector2> corners = [new(0, 0), new(0, 1), new(1, 1), new(0, 0), new(1, 1), new(1, 0)];
            foreach (var point in points)
            {
                var direction = point.CameraDirection;
                Vector3 center = new((float)direction.X, (float)direction.Y, (float)direction.Z);
                foreach (var uv in corners)
                {
                    mesh.SetColor(new(.8f, .88f, 1f, (float)point.Brightness));
                    mesh.SetUV(uv);
                    // The saved sky used a 0.18-unit quad at 100 units from the camera.
                    mesh.AddVertex(center + new Vector3(uv.X - .5f, uv.Y - .5f, 0) * .0018f);
                }
            }
            node.Mesh = mesh.Commit();
            _archivedStarPointSource = points;
        }
        node.Transform = _navigationCamera.Transform;
        node.Show();
    }

    private void DrawArchivedStars(RecordedStellarView? view, double julianDate)
    {
        if (view is null || DisplayServer.GetName() == "headless")
        {
            _archivedStarBackground?.Hide();
            if (_archivedStarBackground is { } backgroundNode)
                backgroundNode.Mesh = null;
            _archivedBackgroundSource = null;
            ClearMovingStars();
            return;
        }

        var background = _archivedStarBackground ??= CreateArchivedStarNode("ArchivedStarBackground");
        if (!ReferenceEquals(_archivedBackgroundSource, view.Background))
        {
            background.Mesh = StarMesh(view.Background, default);
            _archivedBackgroundSource = view.Background;
        }
        Apply(background, (float)((julianDate - 2451545) / 365.25), view.NearCut);

        if (view.Periodic is { } field)
        {
            var moving = _archivedMovingStars ??= CreateArchivedStarNode("ArchivedMovingStars");
            if (!ReferenceEquals(field, _archivedMovingSource) || !view.PeriodicPositions.Equals(_archivedMovingPositions))
            {
                moving.Mesh = StarMesh(field, MemoryMarshal.Cast<byte, float>(view.PeriodicPositions.Span));
                _archivedMovingSource = field;
                _archivedMovingPositions = view.PeriodicPositions;
            }
            Apply(moving, 0, 0);
        }
        else
            ClearMovingStars();

        void Apply(MeshInstance3D node, float years, float nearCut)
        {
            node.Transform = _navigationCamera.Transform;
            var material = (ShaderMaterial)node.MaterialOverride;
            material.SetShaderParameter("sky_to_camera", new Basis(
                Column(view.ToCamera.X), Column(view.ToCamera.Y), Column(view.ToCamera.Z)));
            material.SetShaderParameter("eye_ly", Column(view.Observer));
            material.SetShaderParameter("near_origin", Column(view.NearOrigin));
            material.SetShaderParameter("near_cut", nearCut);
            material.SetShaderParameter("years", years);
            material.SetShaderParameter("faintest", view.FaintestMagnitude);
            material.SetShaderParameter("brightness_scale", view.BrightnessScale);
            material.SetShaderParameter("saturation_point", view.SaturationPoint);
            material.SetShaderParameter("ground_beta", Column(view.Ground?.Beta ?? default));
            material.SetShaderParameter("zenith", Column(view.Ground?.Zenith ?? new(0, 1, 0)));
            material.SetShaderParameter("refraction", view.Ground?.Refraction ?? 0);
            material.SetShaderParameter("twinkle", view.Ground?.Twinkle ?? 0);
            material.SetShaderParameter("animation_time", view.Ground?.Time ?? 0);
            node.Show();
        }

        static Vector3 Column(Vector3d axis) => new((float)axis.X, (float)axis.Y, (float)axis.Z);
    }

    private void ClearMovingStars()
    {
        if (_archivedMovingStars is { } moving)
        {
            moving.Hide();
            moving.Mesh = null;
        }
        _archivedMovingSource = null;
        _archivedMovingPositions = default;
    }

    private MeshInstance3D CreateArchivedStarNode(string name, string shader = "archived_stars")
    {
        var mesh = new MeshInstance3D
        {
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>($"res://Shaders/{shader}.gdshader"), RenderPriority = -10 },
            Name = name,
            Layers = 3,
            CustomAabb = new Aabb(-Vector3.One * 2, Vector3.One * 4),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(mesh);
        return mesh;
    }

    private static ArrayMesh StarMesh(RecordedStarField field, ReadOnlySpan<float> positions)
    {
        var directions = field.Directions;
        var distances = field.Distances;
        var magnitudes = field.Magnitudes;
        var rgb = field.Colors;
        var velocities = field.Velocities;
        var vertices = new Vector3[field.Count];
        var colors = new Color[field.Count];
        var uv = new Vector2[field.Count];
        var motion = velocities.IsEmpty || !positions.IsEmpty ? [] : new float[field.Count * 4];
        for (int i = 0; i < field.Count; i++)
        {
            int component = i * 3;
            float distance = distances.IsEmpty ? 1e9f : distances[i];
            vertices[i] = positions.IsEmpty
                ? new Vector3(directions[component], directions[component + 1], directions[component + 2]) * distance
                : new Vector3(positions[component], positions[component + 1], positions[component + 2]);
            colors[i] = Color.Color8(rgb[component], rgb[component + 1], rgb[component + 2]);
            uv[i] = new(magnitudes[i] - 5 * MathF.Log10(distance), 0);
            if (motion.Length != 0)
                velocities.Slice(component, 3).CopyTo(motion.AsSpan(i * 4, 3));
        }
        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        if (motion.Length != 0)
            arrays[(int)Mesh.ArrayType.Custom0] = motion;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays, flags: motion.Length == 0 ? 0 :
            (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        return mesh;
    }
}
