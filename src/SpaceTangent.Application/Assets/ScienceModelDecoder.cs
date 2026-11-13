// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

public sealed record DecodedMaterial(Vector3d Color, double Opacity, string? Texture)
{
    public string? NormalTexture { get; init; }
    public string? EmissionTexture { get; init; }
    public Vector3d Emission { get; init; }
    public bool AlphaBlend { get; init; }
    public double? AlphaCutoff { get; init; }
}
public sealed record DecodedSurface(ReadOnlyMemory<Vector3d> Positions, ReadOnlyMemory<Vector3d> Normals,
    ReadOnlyMemory<float> TextureCoordinates, DecodedMaterial Material);
public sealed record DecodedScienceModel(IReadOnlyList<DecodedSurface> Surfaces,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Textures, double Diameter)
{
    public IReadOnlyList<DecodedModelSprite> Sprites { get; init; } = [];
}

public readonly record struct DecodedModelSprite(Vector3d Position, double Size, Vector3d Color, double Opacity);

/// <summary>Decodes the local science OBJ convention into managed data for main-thread publication.</summary>
public static class ScienceModelDecoder
{
    private const long MaximumObjBytes = 64L * 1024 * 1024;
    private const long MaximumMtlBytes = 1024 * 1024;
    private const long MaximumTextureBytes = 16L * 1024 * 1024;
    private const int MaximumTriangles = 1_000_000, MaximumSurfaces = 128, MaximumTextureDimension = 8192;
    private readonly record struct Corner(Vector3d Position, float U, float V, Vector3d? Normal);
    private static readonly DecodedMaterial DefaultMaterial = new(new(1, 1, 1), 1, null);

    public static DecodedScienceModel Decode(string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => DecodeCore(path, cancellation));

    private static DecodedScienceModel DecodeCore(string path, CancellationToken cancellation)
    {
        string directory = Path.GetDirectoryName(path)!;
        var positions = new List<Vector3d>();
        var normals = new List<Vector3d>();
        var uvs = new List<(float U, float V)>();
        var surfaces = new Dictionary<string, List<Corner>>(StringComparer.Ordinal);
        var materials = new Dictionary<string, DecodedMaterial>(StringComparer.Ordinal);
        string material = "";
        int triangles = 0;

        foreach (string[] fields in ReadFields(path, MaximumObjBytes, cancellation))
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
                    uvs.Add((Number(fields[1]), 1 - Number(fields[2])));
                    break;
                case "mtllib" when fields.Length == 2:
                    ReadMaterials(directory, fields[1], materials, cancellation);
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
                    triangles += fields.Length - 3;
                    if (triangles > MaximumTriangles)
                        throw new InvalidDataException("OBJ contains too many triangles.");

                    Corner first = ReadCorner(fields[1], positions, uvs, normals);
                    Corner second = ReadCorner(fields[2], positions, uvs, normals);
                    for (int index = 3; index < fields.Length; index++)
                    {
                        Corner third = ReadCorner(fields[index], positions, uvs, normals);
                        AddTriangle(corners, first, second, third);
                        second = third;
                    }
                    break;
            }
        }
        if (triangles == 0)
            throw new InvalidDataException("OBJ contains no triangles.");

        var decoded = new List<DecodedSurface>(surfaces.Count);
        var textures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        foreach (var (name, corners) in surfaces)
        {
            cancellation.ThrowIfCancellationRequested();
            var vertexPositions = new Vector3d[corners.Count];
            var vertexNormals = new Vector3d[corners.Count];
            var vertexUvs = new float[corners.Count * 2];
            for (int index = 0; index < corners.Count; index++)
            {
                Corner corner = corners[index];
                Vector3d p = vertexPositions[index] = corner.Position;
                vertexNormals[index] = corner.Normal!.Value;
                vertexUvs[index * 2] = corner.U;
                vertexUvs[index * 2 + 1] = corner.V;
                minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
                maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
            }
            DecodedMaterial definition = materials.GetValueOrDefault(name) ?? DefaultMaterial;
            if (definition.Texture is { } texture && !textures.ContainsKey(texture))
                textures.Add(texture, AddonDiagnostics.Read(AddonStage.Texture, Path.Combine(directory, texture),
                    () => ReadPng(Companion(directory, texture, ".png"))));
            decoded.Add(new(vertexPositions, vertexNormals, vertexUvs, definition));
        }
        cancellation.ThrowIfCancellationRequested();
        double diameter = (maximum - minimum).Length;
        if (!(diameter > 0 && double.IsFinite(diameter)))
            throw new InvalidDataException("OBJ contains invalid model bounds.");
        return new(decoded.AsReadOnly(), new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(textures), diameter);
    }

    private static byte[] ReadPng(string path)
    {
        using FileStream file = OpenAsset(path, MaximumTextureBytes);
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.AsSpan().StartsWith(signature))
            throw new InvalidDataException("Invalid PNG companion texture.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        if (width is 0 or > MaximumTextureDimension || height is 0 or > MaximumTextureDimension)
            throw new InvalidDataException("PNG companion texture dimensions are outside the supported range.");
        return bytes;
    }

    private static void AddTriangle(List<Corner> output, Corner first, Corner second, Corner third)
    {
        Vector3d cross = Vector3d.Cross(second.Position - first.Position, third.Position - first.Position);
        double length = cross.Length;
        Vector3d normal = length > 0 ? cross / length : default;
        output.Add(first with { Normal = first.Normal ?? normal });
        output.Add(third with { Normal = third.Normal ?? normal });
        output.Add(second with { Normal = second.Normal ?? normal });
    }

    private static Corner ReadCorner(string text, List<Vector3d> positions, List<(float U, float V)> uvs, List<Vector3d> normals)
    {
        string[] indices = text.Split('/');
        Vector3d position = positions[Index(indices[0], positions.Count)];
        var uv = indices.Length > 1 && indices[1].Length > 0 ? uvs[Index(indices[1], uvs.Count)] : default;
        Vector3d? normal = indices.Length > 2 && indices[2].Length > 0 ? normals[Index(indices[2], normals.Count)] : null;
        return new(position, uv.U, uv.V, normal);
    }

    private static int Index(string value, int count)
    {
        int number = int.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int index = number < 0 ? count + number : number - 1;
        if (index < 0 || index >= count)
            throw new InvalidDataException("OBJ vertex index is out of range.");
        return index;
    }

    private static void ReadMaterials(string directory, string name, Dictionary<string, DecodedMaterial> materials,
        CancellationToken cancellation)
    {
        try
        {
            ReadMaterialFile(Companion(directory, name, ".mtl"), materials, cancellation);
        }
        catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Mesh, Path.Combine(directory, name)))
        {
            throw;
        }
    }

    private static void ReadMaterialFile(string path, Dictionary<string, DecodedMaterial> materials, CancellationToken cancellation)
    {
        string? name = null;
        DecodedMaterial value = DefaultMaterial;
        foreach (string[] fields in ReadFields(path, MaximumMtlBytes, cancellation))
        {
            switch (fields[0])
            {
                case "newmtl" when fields.Length > 1:
                    if (name is not null)
                        materials[name] = value;
                    name = string.Join(' ', fields.Skip(1));
                    value = DefaultMaterial;
                    break;
                case "Kd" when fields.Length >= 4:
                    value = value with { Color = new(ClampColor(fields[1]), ClampColor(fields[2]), ClampColor(fields[3])) };
                    break;
                case "d" or "Tr" when fields.Length == 2:
                    value = value with { Opacity = ClampColor(fields[1]) };
                    break;
                case "map_Kd" when fields.Length > 1:
                    value = value with { Texture = fields[^1] };
                    break;
            }
        }
        if (name is not null)
            materials[name] = value;
    }

    private static string Companion(string directory, string name, string extension)
    {
        if (Path.GetFileName(name) != name || !Path.GetExtension(name).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OBJ companion assets must use a local filename and supported extension.");
        string path = Path.Combine(directory, name);
        if ((AddonDependencies.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OBJ companion assets must not use symbolic links.");
        return path;
    }

    private static IEnumerable<string[]> ReadFields(string path, long limit, CancellationToken cancellation)
    {
        using var reader = new StreamReader(OpenAsset(path, limit));
        while (reader.ReadLine() is { } line)
        {
            cancellation.ThrowIfCancellationRequested();
            int comment = line.IndexOf('#');
            string[] fields = (comment < 0 ? line : line[..comment]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 0)
                yield return fields;
        }
    }

    private static FileStream OpenAsset(string path, long limit)
    {
        FileStream file = AddonDependencies.OpenRead(path, limit);
        if (file.Length <= limit)
            return file;
        file.Dispose();
        throw new InvalidDataException("OBJ or companion file exceeds the supported size.");
    }

    private static Vector3d ReadVector(string[] fields) => new(Number(fields[1]), Number(fields[2]), Number(fields[3]));
    private static float ClampColor(string text) => Math.Clamp(Number(text), 0, 1);

    private static float Number(string text)
    {
        float value = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return float.IsFinite(value) ? value : throw new InvalidDataException("OBJ number must be finite.");
    }
}
