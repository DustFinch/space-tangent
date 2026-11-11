// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;

namespace SpaceTangent.Godot.Simulation;

internal static class ScienceMeshLoader
{
    private const long MaximumObjBytes = 64L * 1024 * 1024;
    private const long MaximumMtlBytes = 1024 * 1024;
    private const long MaximumTextureBytes = 16L * 1024 * 1024;
    private const int MaximumTriangles = 1_000_000;
    private const int MaximumSurfaces = 128;
    private readonly record struct Corner(Vector3 Position, Vector2 UV, Vector3? Normal);
    private sealed record MaterialSpec(Color Diffuse, string? Texture);

    internal static ArrayMesh Load(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var surfaces = new Dictionary<string, List<Corner>>(StringComparer.Ordinal);
        var materialLibrary = new Dictionary<string, MaterialSpec>(StringComparer.Ordinal);
        string material = "";
        int triangleCount = 0;

        foreach (var fields in ReadFields(path, MaximumObjBytes))
        {
            switch (fields[0])
            {
                case "v" when fields.Length >= 4:
                    positions.Add(ReadVector(fields));
                    break;
                case "vn" when fields.Length >= 4:
                    normals.Add(ReadVector(fields));
                    break;
                case "vt" when fields.Length >= 3:
                    uvs.Add(new(Number(fields[1]), 1 - Number(fields[2])));
                    break;
                case "mtllib" when fields.Length == 2:
                    ReadMaterials(Companion(directory, fields[1], ".mtl"), materialLibrary);
                    break;
                case "usemtl" when fields.Length > 1:
                    material = string.Join(' ', fields.Skip(1));
                    break;
                case "f" when fields.Length >= 4:
                    if (!surfaces.TryGetValue(material, out var corners))
                    {
                        if (surfaces.Count == MaximumSurfaces)
                            throw new InvalidDataException("OBJ contains too many material groups.");
                        surfaces.Add(material, corners = []);
                    }
                    triangleCount += fields.Length - 3;
                    if (triangleCount > MaximumTriangles)
                        throw new InvalidDataException("OBJ contains too many triangles.");

                    Corner first = ReadCorner(fields[1], positions, uvs, normals);
                    Corner second = ReadCorner(fields[2], positions, uvs, normals);
                    for (int i = 3; i < fields.Length; i++)
                    {
                        Corner third = ReadCorner(fields[i], positions, uvs, normals);
                        AddTriangle(corners, first, second, third);
                        second = third;
                    }
                    break;
            }
        }

        if (triangleCount == 0)
            throw new InvalidDataException("OBJ contains no triangles.");
        var mesh = new ArrayMesh();
        var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        foreach (var (name, corners) in surfaces)
        {
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = corners.Select(corner => corner.Position).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = corners.Select(corner => corner.Normal!.Value).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = corners.Select(corner => corner.UV).ToArray();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var spec = materialLibrary.GetValueOrDefault(name) ?? new MaterialSpec(Colors.White, null);
            var godotMaterial = new StandardMaterial3D
            {
                AlbedoColor = spec.Diffuse,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Transparency = spec.Diffuse.A < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled
            };
            if (spec.Texture is { } texture)
            {
                if (!textures.TryGetValue(texture, out var imageTexture))
                {
                    string source = Companion(directory, texture, ".png");
                    CheckSize(source, MaximumTextureBytes);
                    using Image image = Image.LoadFromFile(source);
                    if (image is null || image.IsEmpty())
                        throw new InvalidDataException("Could not decode OBJ diffuse texture.");
                    textures.Add(texture, imageTexture = ImageTexture.CreateFromImage(image));
                }
                godotMaterial.AlbedoTexture = imageTexture;
            }
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, godotMaterial);
        }
        return mesh;
    }

    private static void AddTriangle(List<Corner> corners, Corner first, Corner second, Corner third)
    {
        Vector3 normal = (second.Position - first.Position).Cross(third.Position - first.Position).Normalized();
        corners.Add(first with { Normal = first.Normal ?? normal });
        corners.Add(third with { Normal = third.Normal ?? normal });
        corners.Add(second with { Normal = second.Normal ?? normal });
    }

    private static Corner ReadCorner(string value, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals)
    {
        string[] indices = value.Split('/');
        int position = Index(indices[0], positions.Count);
        Vector2 uv = indices.Length > 1 && indices[1].Length > 0 ? uvs[Index(indices[1], uvs.Count)] : default;
        Vector3? normal = indices.Length > 2 && indices[2].Length > 0 ? normals[Index(indices[2], normals.Count)] : null;
        return new(positions[position], uv, normal);
    }

    private static int Index(string value, int count)
    {
        int number = int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int index = number < 0 ? count + number : number - 1;
        if (index < 0 || index >= count)
            throw new InvalidDataException("OBJ vertex index is out of range.");
        return index;
    }

    private static void ReadMaterials(string path, Dictionary<string, MaterialSpec> materials)
    {
        string? name = null;
        Color diffuse = Colors.White;
        string? texture = null;

        foreach (var fields in ReadFields(path, MaximumMtlBytes))
        {
            switch (fields[0])
            {
                case "newmtl" when fields.Length > 1:
                    if (name is not null)
                        materials[name] = new(diffuse, texture);
                    name = string.Join(' ', fields.Skip(1));
                    diffuse = Colors.White;
                    texture = null;
                    break;
                case "Kd" when fields.Length >= 4:
                    diffuse = new(ClampColor(fields[1]), ClampColor(fields[2]), ClampColor(fields[3]), diffuse.A);
                    break;
                case "d" or "Tr" when fields.Length == 2:
                    // The source convention treats both values as opacity.
                    diffuse.A = ClampColor(fields[1]);
                    break;
                case "map_Kd" when fields.Length > 1:
                    texture = fields[^1];
                    break;
            }
        }
        if (name is not null)
            materials[name] = new(diffuse, texture);
    }

    private static string Companion(string directory, string name, string extension)
    {
        if (Path.GetFileName(name) != name || !Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OBJ companion assets must use a local filename and supported extension.");
        string path = Path.Combine(directory, name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OBJ companion assets must not use symbolic links.");
        return path;
    }

    private static IEnumerable<string[]> ReadFields(string path, long limit)
    {
        CheckSize(path, limit);
        foreach (string line in File.ReadLines(path))
        {
            int comment = line.IndexOf('#');
            string[] fields = (comment < 0 ? line : line[..comment])
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0)
                yield return fields;
        }
    }

    private static void CheckSize(string path, long limit)
    {
        if (new FileInfo(path).Length > limit)
            throw new InvalidDataException("OBJ or companion file exceeds the supported size.");
    }

    private static Vector3 ReadVector(string[] fields) => new(Number(fields[1]), Number(fields[2]), Number(fields[3]));
    private static float ClampColor(string text) => Math.Clamp(Number(text), 0, 1);

    private static float Number(string text)
    {
        float value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return float.IsFinite(value) ? value : throw new InvalidDataException("OBJ number must be finite.");
    }
}
