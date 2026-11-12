// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static class ScienceMeshLoader
{
    internal static ArrayMesh Publish(DecodedScienceModel source, string meshPath, bool litBody = false) =>
        AddonDiagnostics.Read(AddonStage.RenderResources, meshPath, () => PublishCore(source, meshPath, litBody));

    private static ArrayMesh PublishCore(DecodedScienceModel source, string meshPath, bool litBody)
    {
        var mesh = new ArrayMesh();
        var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        Shader? shader = litBody ? GD.Load<Shader>("res://Shaders/planet_moon_model.gdshader") : null;
        foreach (DecodedSurface surface in source.Surfaces)
        {
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            Vector3[] vertices = ConvertVectors(surface.Positions.Span);
            Vector3[] normals = ConvertVectors(surface.Normals.Span);
            Vector2[] uvs = ConvertUvs(surface.TextureCoordinates.Span);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            DecodedMaterial definition = surface.Material;
            var color = new Color((float)definition.Color.X, (float)definition.Color.Y,
                (float)definition.Color.Z, (float)definition.Opacity);
            Texture2D? albedo = definition.Texture is { } texture ? Texture(texture) : null;
            Material material;
            if (shader is not null)
            {
                var body = new ShaderMaterial { Shader = shader };
                body.SetShaderParameter("base_color", color);
                if (albedo is not null)
                {
                    body.SetShaderParameter("has_diffuse", true);
                    body.SetShaderParameter("diffuse_texture", albedo);
                }
                if (definition.NormalTexture is { } normalMap)
                {
                    body.SetShaderParameter("has_normal", true);
                    body.SetShaderParameter("normal_texture", Texture(normalMap));
                    body.SetShaderParameter("packed_normal", Dds(normalMap));
                    arrays[(int)Mesh.ArrayType.Tangent] = Tangents(vertices, normals, uvs);
                }
                material = body;
            }
            else
            {
                material = new StandardMaterial3D
                {
                    AlbedoColor = color,
                    AlbedoTexture = albedo,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    Transparency = definition.Opacity < 1
                        ? BaseMaterial3D.TransparencyEnum.Alpha
                        : BaseMaterial3D.TransparencyEnum.Disabled
                };
            }
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return mesh;

        Texture2D Texture(string name)
        {
            if (textures.TryGetValue(name, out var available))
                return available;
            using var image = new Image();
            var data = source.Textures[name].ToArray();
            Error status = Dds(name) ? image.LoadDdsFromBuffer(data) : image.LoadPngFromBuffer(data);
            if (status != Error.Ok || image.IsEmpty())
            {
                var error = new InvalidDataException("Could not decode the model's texture.");
                AddonDiagnostics.Annotate(error, AddonStage.Texture, Path.Combine(Path.GetDirectoryName(meshPath)!, name));
                throw error;
            }
            if (litBody && !image.IsCompressed() && !image.HasMipmaps())
                image.GenerateMipmaps();
            textures.Add(name, available = ImageTexture.CreateFromImage(image));
            return available;
        }
    }

    private static bool Dds(string path) => Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase);

    private static float[] Tangents(Vector3[] vertices, Vector3[] normals, Vector2[] uvs)
    {
        var tangents = new float[vertices.Length * 4];
        for (int index = 0; index < vertices.Length; index += 3)
        {
            Vector3 a = vertices[index + 1] - vertices[index], b = vertices[index + 2] - vertices[index];
            Vector2 u = uvs[index + 1] - uvs[index], v = uvs[index + 2] - uvs[index];
            float determinant = u.Cross(v);
            Vector3 along = default, across = default;
            if (Math.Abs(determinant) > 1e-12f)
            {
                along = (a * v.Y - b * u.Y) / determinant;
                across = (b * u.X - a * v.X) / determinant;
            }
            for (int corner = index; corner < index + 3; corner++)
            {
                Vector3 normal = normals[corner].Normalized();
                Vector3 tangent = (along - normal * along.Dot(normal)).Normalized();
                if (tangent.LengthSquared() < .01f)
                    tangent = normal.Cross(Math.Abs(normal.Y) < .9f ? Vector3.Up : Vector3.Right).Normalized();
                tangents[corner * 4] = tangent.X;
                tangents[corner * 4 + 1] = tangent.Y;
                tangents[corner * 4 + 2] = tangent.Z;
                tangents[corner * 4 + 3] = normal.Cross(tangent).Dot(across) < 0 ? -1 : 1;
            }
        }
        return tangents;
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
