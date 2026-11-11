// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static class ScienceMeshLoader
{
    internal static ArrayMesh Publish(DecodedScienceModel source, string meshPath) =>
        AddonDiagnostics.Read(AddonStage.RenderResources, meshPath, () => PublishCore(source, meshPath));

    private static ArrayMesh PublishCore(DecodedScienceModel source, string meshPath)
    {
        var mesh = new ArrayMesh();
        var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        foreach (DecodedSurface surface in source.Surfaces)
        {
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = ConvertVectors(surface.Positions.Span);
            arrays[(int)Mesh.ArrayType.Normal] = ConvertVectors(surface.Normals.Span);
            arrays[(int)Mesh.ArrayType.TexUV] = ConvertUvs(surface.TextureCoordinates.Span);
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            DecodedMaterial definition = surface.Material;
            var material = new StandardMaterial3D
            {
                AlbedoColor = new((float)definition.Color.X, (float)definition.Color.Y,
                    (float)definition.Color.Z, (float)definition.Opacity),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = definition.Opacity < 1
                    ? BaseMaterial3D.TransparencyEnum.Alpha
                    : BaseMaterial3D.TransparencyEnum.Disabled
            };
            if (definition.Texture is { } texture)
            {
                if (!textures.TryGetValue(texture, out var gpuTexture))
                {
                    using var image = new Image();
                    Error status = image.LoadPngFromBuffer(source.Textures[texture].ToArray());
                    if (status != Error.Ok || image.IsEmpty())
                    {
                        var error = new InvalidDataException("Could not decode the model's PNG texture.");
                        AddonDiagnostics.Annotate(error, AddonStage.Texture,
                            Path.Combine(Path.GetDirectoryName(meshPath)!, texture));
                        throw error;
                    }
                    textures.Add(texture, gpuTexture = ImageTexture.CreateFromImage(image));
                }
                material.AlbedoTexture = gpuTexture;
            }
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return mesh;
    }

    private static Vector3[] ConvertVectors(ReadOnlySpan<Vector3d> source)
    {
        var output = new Vector3[source.Length];
        for (int i = 0; i < source.Length; i++)
            output[i] = new((float)source[i].X, (float)source[i].Y, (float)source[i].Z);
        return output;
    }

    private static Vector2[] ConvertUvs(ReadOnlySpan<float> source)
    {
        var output = new Vector2[source.Length / 2];
        for (int i = 0; i < output.Length; i++)
            output[i] = new(source[i * 2], source[i * 2 + 1]);
        return output;
    }
}
