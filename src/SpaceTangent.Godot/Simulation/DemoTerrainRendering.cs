// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class DemoTerrainRendering(Node owner, ProceduralTerrain terrain, double radiusKilometers, ShaderMaterial globeMaterial)
{
    private sealed record Patch(Vector3d Center, MeshInstance3D Node);
    private sealed record PatchData(ProceduralTerrainPatch Surface, float[] FirstHorizons, float[] LastHorizons);
    private sealed record Work(ProceduralPatchKey[] Selected, Dictionary<ProceduralPatchKey, PatchData> Built);
    private readonly Dictionary<ProceduralPatchKey, Patch> _cache = new();
    private readonly HashSet<ProceduralPatchKey> _shown = [];
    private ProceduralPatchKey[] _ready = [];
    private Task<Work>? _pending;
    private (Vector3d Eye, Vector3d Look, float Fov, Vector2 Size)? _requested;
    private ShaderMaterial? _material;

    internal Vector3? ProbeLightDirection { get; set; }
    internal void SetShadows(bool enabled) => _material?.SetShaderParameter("terrain_shadows", enabled);

    internal bool Visible => _shown.Count != 0;
    internal int Level => _shown.Count == 0 ? -1 : _shown.Max(key => key.Level);
    internal double GroundRadius(Vector3d direction) => terrain.GroundRadiusKilometers(direction);

    internal bool Draw(Vector3d eye, Vector3d look, Vector3 eyePosition, Basis rotation, double units,
        float fov, Vector2 size)
    {
        double height = Math.Max(.05, eye.Length - GroundRadius(eye / eye.Length));
        if (_pending is null && (_requested is not { } last ||
            (last.Eye - eye).Length > height * .06 || (last.Look - look).Length > .012 ||
            last.Fov != fov || last.Size != size))
        {
            _requested = (eye, look, fov, size);
            double tangent = Math.Tan(fov * Math.PI / 360);
            double aspect = size.X / Math.Max(1, size.Y);
            double cone = Math.Atan(tangent * Math.Sqrt(1 + aspect * aspect)) + .15;
            double focal = Math.Max(1, size.Y) / (2 * tangent);
            var cached = _cache.Keys.ToHashSet();
            _pending = Task.Run(() =>
            {
                var selected = terrain.Select(eye, look, cone, focal).ToArray();
                return new Work(selected, selected.Where(key => !cached.Contains(key)).ToDictionary(key => key, BuildPatch));
            });
        }

        if (_ready.Length == 0)
            return false;
        _material ??= (ShaderMaterial)globeMaterial.Duplicate();
        CopyUniforms();
        if (ProbeLightDirection is { } light)
        {
            _material.SetShaderParameter("first_direction", light);
            _material.SetShaderParameter("first_irradiance", Vector3.One);
            _material.SetShaderParameter("second_irradiance", Vector3.Zero);
        }
        _material.SetShaderParameter("use_terrain", true);
        _material.SetShaderParameter("terrain_eye", Native(eye / radiusKilometers));
        var basis = rotation.Scaled(Vector3.One * (float)(radiusKilometers / units));
        foreach (var key in _ready)
        {
            var patch = _cache[key];
            patch.Node.MaterialOverride = _material;
            patch.Node.Transform = new(basis, eyePosition + rotation * Native((patch.Center - eye) / units));
            patch.Node.Show();
            _shown.Add(key);
        }
        return true;
    }

    internal bool Publish()
    {
        if (_pending is not { IsCompleted: true } completed)
            return false;
        _pending = null;
        if (!completed.IsCompletedSuccessfully)
        {
            GD.PushWarning($"Could not generate procedural terrain: {completed.Exception?.GetBaseException().Message}");
            return false;
        }

        var work = completed.Result;
        foreach (var (key, data) in work.Built)
        {
            var node = new MeshInstance3D { Mesh = Mesh(data), Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            owner.AddChild(node);
            _cache.Add(key, new(data.Surface.CenterKilometers, node));
        }
        _ready = work.Selected;
        if (_cache.Count > ProceduralTerrain.MaximumLeaves * 2)
        {
            var retained = _ready.ToHashSet();
            foreach (var key in _cache.Keys.Where(key => !retained.Contains(key)).ToArray())
            {
                _shown.Remove(key);
                _cache[key].Node.QueueFree();
                _cache.Remove(key);
            }
        }
        return true;
    }

    internal void Hide()
    {
        foreach (var key in _shown)
            _cache[key].Node.Hide();
        _shown.Clear();
    }

    private static readonly string[] MaterialParameters =
    [
        "albedo_map", "normal_map", "cloud_map", "emission_map", "rings_map", "fallback_color", "has_maps",
        "has_clouds", "has_emission", "has_rings", "draw_air", "cloud_height", "ring_inner", "ring_outer",
        "first_direction", "first_irradiance", "second_direction", "second_irradiance", "demo_radius",
        "demo_air_top", "demo_rayleigh", "demo_absorption", "demo_mie_albedo", "demo_mie", "demo_mie_g",
        "demo_rayleigh_height", "demo_mie_height"
    ];

    private void CopyUniforms()
    {
        foreach (string name in MaterialParameters)
        {
            var value = globeMaterial.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil)
                _material!.SetShaderParameter(name, value);
        }
    }

    private PatchData BuildPatch(ProceduralPatchKey key)
    {
        var data = terrain.Build(key);
        const int grid = ProceduralTerrain.Grid;
        const int row = grid + 1;
        const int stride = 4;
        const int coarseStride = 2 * stride;
        var fine = Sample(data.Vertices, stride, data.VertexSpacingKilometers);
        var coarse = data.ParentVertices is { } parent
            ? Sample(parent, coarseStride, 2 * data.VertexSpacingKilometers) : fine;
        var first = new float[4 * data.Vertices.Length];
        var last = new float[4 * data.Vertices.Length];
        for (int i = 0; i < data.Vertices.Length; i++)
        {
            var (x, y) = GridPoint(i);
            for (int a = 0; a < ProceduralTerrain.HorizonAzimuths; a++)
            {
                float low = Interpolate(fine, stride, x, y, a);
                float high = data.ParentVertices is null ? low : Interpolate(coarse, coarseStride, x, y, a);
                (a < 4 ? first : last)[4 * i + a % 4] = Quantize(low) * 256 + Quantize(high);
            }
        }
        return new(data, first, last);

        float[] Sample(ProceduralTerrainVertex[] vertices, int interval, double spacing)
        {
            int samples = grid / interval + 1;
            const int azimuths = ProceduralTerrain.HorizonAzimuths;
            var heights = new float[samples * samples * azimuths];
            for (int y = 0; y < samples; y++)
            for (int x = 0; x < samples; x++)
                terrain.SampleHorizon(vertices[y * interval * row + x * interval].PositionKilometers +
                    data.CenterKilometers, spacing, heights.AsSpan((y * samples + x) * azimuths, azimuths));
            return heights;
        }

        static (int X, int Y) GridPoint(int vertex)
        {
            if (vertex < row * row)
                return (vertex % row, vertex / row);
            int skirt = vertex - row * row, offset = skirt % grid;
            return (skirt / grid) switch
            {
                0 => (offset, 0), 1 => (grid, offset), 2 => (grid - offset, grid), _ => (0, grid - offset)
            };
        }

        static float Interpolate(float[] values, int interval, int x, int y, int azimuth)
        {
            int samples = grid / interval + 1;
            int left = Math.Min(x / interval, samples - 2), top = Math.Min(y / interval, samples - 2);
            float sx = (float)x / interval - left, sy = (float)y / interval - top;
            return Mathf.Lerp(Mathf.Lerp(Read(left, top), Read(left + 1, top), sx),
                Mathf.Lerp(Read(left, top + 1), Read(left + 1, top + 1), sx), sy);

            float Read(int column, int line) => values[(line * samples + column) * ProceduralTerrain.HorizonAzimuths + azimuth];
        }

        static int Quantize(float value) => Mathf.RoundToInt(Mathf.Clamp((value + .25f) / 1.25f, 0, 1) * 255);
    }

    private ArrayMesh Mesh(PatchData patch)
    {
        var data = patch.Surface;
        var vertices = new Vector3[data.Vertices.Length];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        // Custom attributes carry the parent position, octahedral normal and material.
        // Each horizon component packs the fine and parent samples in two bytes.
        var parentOffsets = new float[4 * vertices.Length];
        var parentNormals = new float[4 * vertices.Length];
        var parentColors = new float[4 * vertices.Length];

        // Encode the patch centre without per-instance uniforms, whose buffer is shared across Compatibility viewports.
        Vector3 center = Native(data.CenterKilometers / radiusKilometers);
        var uv = Enumerable.Repeat(new Vector2(center.X, center.Y), vertices.Length).ToArray();
        var secondUv = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var sample = data.Vertices[i];
            vertices[i] = Native(sample.PositionKilometers / radiusKilometers);
            normals[i] = Native(sample.Normal);
            colors[i] = new(sample.Albedo.Red, sample.Albedo.Green, sample.Albedo.Blue, sample.Albedo.Water);
            var coarse = data.ParentVertices?[i] ?? sample;
            var offset = (coarse.PositionKilometers - sample.PositionKilometers) / radiusKilometers;
            parentOffsets[4 * i] = (float)offset.X;
            parentOffsets[4 * i + 1] = (float)offset.Y;
            parentOffsets[4 * i + 2] = (float)offset.Z;
            parentOffsets[4 * i + 3] = data.ParentVertices is null ? 0 : (float)(data.VertexSpacingKilometers / radiusKilometers);
            float normalX = (float)coarse.Normal.X, normalY = (float)coarse.Normal.Y, normalZ = (float)coarse.Normal.Z;
            var normal = new Vector2(normalX, normalY) / (Mathf.Abs(normalX) + Mathf.Abs(normalY) + Mathf.Abs(normalZ));
            if (normalZ < 0)
                normal = new((1 - Mathf.Abs(normal.Y)) * (normal.X < 0 ? -1 : 1),
                    (1 - Mathf.Abs(normal.X)) * (normal.Y < 0 ? -1 : 1));
            parentNormals[4 * i] = normal.X;
            parentNormals[4 * i + 1] = normal.Y;
            secondUv[i] = new(center.Z, patch.LastHorizons[4 * i]);
            parentNormals[4 * i + 3] = patch.LastHorizons[4 * i + 1];
            parentColors[4 * i] = coarse.Albedo.Red;
            parentColors[4 * i + 1] = coarse.Albedo.Green;
            parentColors[4 * i + 3] = patch.LastHorizons[4 * i + 2];
            parentColors[4 * i + 2] = Mathf.RoundToInt(coarse.Albedo.Blue * 255) * 256 + Mathf.RoundToInt(coarse.Albedo.Water * 255);
            parentNormals[4 * i + 2] = patch.LastHorizons[4 * i + 3];
        }
        int[] indices = (int[])data.Indices.Clone();
        for (int i = 0; i < indices.Length; i += 3)
            (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)global::Godot.Mesh.ArrayType.Max);
        arrays[(int)global::Godot.Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)global::Godot.Mesh.ArrayType.Normal] = normals;
        arrays[(int)global::Godot.Mesh.ArrayType.Color] = colors;
        arrays[(int)global::Godot.Mesh.ArrayType.TexUV] = uv;
        arrays[(int)global::Godot.Mesh.ArrayType.TexUV2] = secondUv;
        arrays[(int)global::Godot.Mesh.ArrayType.Index] = indices;
        arrays[(int)global::Godot.Mesh.ArrayType.Custom0] = parentOffsets;
        arrays[(int)global::Godot.Mesh.ArrayType.Custom1] = parentNormals;
        arrays[(int)global::Godot.Mesh.ArrayType.Custom2] = parentColors;
        arrays[(int)global::Godot.Mesh.ArrayType.Custom3] = patch.FirstHorizons;
        const ulong format = ((ulong)global::Godot.Mesh.ArrayCustomFormat.RgbaFloat << (int)global::Godot.Mesh.ArrayFormat.FormatCustom0Shift) |
            ((ulong)global::Godot.Mesh.ArrayCustomFormat.RgbaFloat << (int)global::Godot.Mesh.ArrayFormat.FormatCustom1Shift) |
            ((ulong)global::Godot.Mesh.ArrayCustomFormat.RgbaFloat << (int)global::Godot.Mesh.ArrayFormat.FormatCustom2Shift) |
            ((ulong)global::Godot.Mesh.ArrayCustomFormat.RgbaFloat << (int)global::Godot.Mesh.ArrayFormat.FormatCustom3Shift);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(global::Godot.Mesh.PrimitiveType.Triangles, arrays, flags: (global::Godot.Mesh.ArrayFormat)format);
        return mesh;
    }

    private static Vector3 Native(Vector3d vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);
}
