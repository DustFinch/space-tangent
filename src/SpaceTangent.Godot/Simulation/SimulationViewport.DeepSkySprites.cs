// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private IReadOnlyDictionary<ObjectId, IReadOnlyList<DecodedModelSprite>>? _deepSkySpriteSource;
    private readonly Dictionary<ObjectId, ArrayMesh> _deepSkySpriteMeshes = new();
    private ShaderMaterial? _deepSkySpriteMaterial;

    internal void SetDeepSkySprites(IReadOnlyDictionary<ObjectId, IReadOnlyList<DecodedModelSprite>>? source)
    {
        if (ReferenceEquals(source, _deepSkySpriteSource))
            return;
        _deepSkySpriteSource = source;
        _deepSkySpriteMeshes.Clear();
    }

    private ArrayMesh? DeepSkySpriteMesh(ObjectId id, Basis3d orientation)
    {
        if (_deepSkySpriteSource is null || !_deepSkySpriteSource.TryGetValue(id, out var sprites))
            return null;
        if (_deepSkySpriteMeshes.TryGetValue(id, out var existing))
            return existing;

        var points = new Vector3[sprites.Count * 4];
        var corners = new Vector2[points.Length];
        var sizes = new Vector2[points.Length];
        var colors = new Color[points.Length];
        var indices = new int[sprites.Count * 6];
        var rotation = orientation.Transpose();
        for (int index = 0; index < sprites.Count; index++)
        {
            var sprite = sprites[index];
            var point = rotation.Transform(sprite.Position);
            var center = new Vector3((float)point.X, (float)point.Z, (float)-point.Y);
            var color = new Color((float)sprite.Color.X, (float)sprite.Color.Y, (float)sprite.Color.Z, (float)sprite.Opacity);
            int corner = index * 4;
            for (int i = 0; i < 4; i++)
            {
                points[corner + i] = center + new Vector3(i % 2 - .5f, i / 2 - .5f, 0) * (float)sprite.Size;
                corners[corner + i] = new(i % 2, i / 2);
                sizes[corner + i] = new((float)sprite.Size, 0);
                colors[corner + i] = color;
            }
            int face = index * 6;
            indices[face] = corner;
            indices[face + 1] = corner + 1;
            indices[face + 2] = corner + 2;
            indices[face + 3] = corner + 1;
            indices[face + 4] = corner + 3;
            indices[face + 5] = corner + 2;
        }

        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;
        arrays[(int)Mesh.ArrayType.TexUV] = corners;
        arrays[(int)Mesh.ArrayType.TexUV2] = sizes;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = mesh.GetAabb().Grow(sizes.Max(size => size.X) * .5f);
        _deepSkySpriteMaterial ??= new() { Shader = GD.Load<Shader>("res://Shaders/nebula_sprites.gdshader") };
        mesh.SurfaceSetMaterial(0, _deepSkySpriteMaterial);
        _deepSkySpriteMeshes.Add(id, mesh);
        GD.Print($"Deep sky GPU: {id.Value}: uploaded {sprites.Count} model sprites.");
        return mesh;
    }
}
