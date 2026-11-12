// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class DemoTerrainRendering(Node owner, ProceduralTerrain terrain, double radiusKilometers, ShaderMaterial globeMaterial)
{
    private sealed record Patch(Vector3d Center, MeshInstance3D Node);
    private sealed record Work(ProceduralPatchKey[] Selected, Dictionary<ProceduralPatchKey, ProceduralTerrainPatch> Built);
    private readonly Dictionary<ProceduralPatchKey, Patch> _cache = new();
    private readonly HashSet<ProceduralPatchKey> _shown = [];
    private ProceduralPatchKey[] _ready = [];
    private Task<Work>? _pending;
    private (Vector3d Eye, Vector3d Look, float Fov, Vector2 Size)? _requested;
    private ShaderMaterial? _material;

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
                return new Work(selected, selected.Where(key => !cached.Contains(key)).ToDictionary(key => key, terrain.Build));
            });
        }

        if (_ready.Length == 0)
            return false;
        _material ??= (ShaderMaterial)globeMaterial.Duplicate();
        CopyUniforms();
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
            _cache.Add(key, new(data.CenterKilometers, node));
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
        "has_clouds", "has_emission", "has_rings", "cloud_height", "ring_inner", "ring_outer",
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

    private ArrayMesh Mesh(ProceduralTerrainPatch data)
    {
        var vertices = new Vector3[data.Vertices.Length];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        // Encode the patch centre without per-instance uniforms, whose buffer is shared across Compatibility viewports.
        Vector3 center = Native(data.CenterKilometers / radiusKilometers);
        var uv = Enumerable.Repeat(new Vector2(center.X, center.Y), vertices.Length).ToArray();
        var secondUv = Enumerable.Repeat(new Vector2(center.Z, 0), vertices.Length).ToArray();
        for (int i = 0; i < vertices.Length; i++)
        {
            var sample = data.Vertices[i];
            vertices[i] = Native(sample.PositionKilometers / radiusKilometers);
            normals[i] = Native(sample.Normal);
            colors[i] = new(sample.Albedo.Red, sample.Albedo.Green, sample.Albedo.Blue, sample.Albedo.Water);
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
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(global::Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static Vector3 Native(Vector3d vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);
}
