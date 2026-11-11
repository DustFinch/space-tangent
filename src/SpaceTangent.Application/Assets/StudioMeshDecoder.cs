// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application.Assets;

/// <summary>Decodes static, untextured 3DS triangle meshes with diffuse materials.</summary>
public static class StudioMeshDecoder
{
    private const int MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumTriangles = 1_000_000;
    private const int MaximumSurfaces = 128;
    private static readonly DecodedMaterial DefaultMaterial = new(new(.8, .8, .8), 1, null);

    private readonly record struct Triangle(int A, int B, int C);
    private sealed record Part(Vector3d[] Vertices, Triangle[] Faces, string[] Materials);
    private sealed record Surface(List<Vector3d> Points, List<Vector3d> Normals);
    private delegate void ChunkVisitor(ushort id, ReadOnlySpan<byte> data);

    public static DecodedScienceModel Decode(string path, CancellationToken cancellation = default)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is < 6 or > MaximumFileBytes)
            throw new InvalidDataException("3DS file size is outside the supported range.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (ReadU16(bytes) != 0x4d4d || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(2)) != bytes.Length)
            throw new InvalidDataException("Invalid 3DS root chunk.");

        var materials = new Dictionary<string, DecodedMaterial>(StringComparer.Ordinal);
        var surfaces = new Dictionary<string, Surface>(StringComparer.Ordinal);
        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        int triangleCount = 0;

        ReadChunks(bytes.AsSpan(6), (id, payload) =>
        {
            if (id != 0x3d3d)
                return;
            ReadChunks(payload, (kind, data) =>
            {
                cancellation.ThrowIfCancellationRequested();
                switch (kind)
                {
                    case 0xafff:
                        var (name, material) = ReadMaterial(data);
                        if (materials.Count >= MaximumSurfaces || !materials.TryAdd(name, material))
                            throw new InvalidDataException("Duplicate or excessive 3DS material definitions.");
                        break;
                    case 0x4000:
                        ReadName(data, out int offset);
                        ReadChunks(data[offset..], (objectKind, objectData) =>
                        {
                            if (objectKind == 0x4100)
                                AddPart(ReadPart(objectData));
                        });
                        break;
                }
            });
        });

        double diameter = (max - min).Length;
        if (surfaces.Count == 0 || !double.IsFinite(diameter) || diameter <= 0)
            throw new InvalidDataException("3DS file has no usable triangle geometry.");

        var result = new List<DecodedSurface>(surfaces.Count);
        foreach (var (name, surface) in surfaces)
        {
            cancellation.ThrowIfCancellationRequested();
            result.Add(new(surface.Points.ToArray(), surface.Normals.ToArray(), new float[surface.Points.Count * 2],
                materials.GetValueOrDefault(name) ?? DefaultMaterial));
        }
        return new(result.AsReadOnly(), new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(
            new Dictionary<string, ReadOnlyMemory<byte>>()), diameter);

        void AddPart(Part part)
        {
            triangleCount += part.Faces.Length;
            if (triangleCount > MaximumTriangles)
                throw new InvalidDataException("3DS file exceeds the supported triangle count.");
            for (int index = 0; index < part.Faces.Length; index++)
            {
                if ((index & 1023) == 0)
                    cancellation.ThrowIfCancellationRequested();
                var (a, b, c) = part.Faces[index];
                if (a >= part.Vertices.Length || b >= part.Vertices.Length || c >= part.Vertices.Length)
                    throw new InvalidDataException("3DS face refers to a missing vertex.");
                Vector3d first = part.Vertices[a], second = part.Vertices[b], third = part.Vertices[c];
                Vector3d cross = Vector3d.Cross(second - first, third - first);
                double length = cross.Length;
                Vector3d normal = length > 0 ? cross / length : new(0, 0, 1);
                string name = part.Materials[index] ?? "";
                if (!surfaces.TryGetValue(name, out var surface))
                {
                    if (surfaces.Count >= MaximumSurfaces)
                        throw new InvalidDataException("3DS file exceeds the supported material surface count.");
                    surfaces.Add(name, surface = new([], []));
                }
                Append(first);
                Append(second);
                Append(third);

                void Append(Vector3d point)
                {
                    surface.Points.Add(point);
                    surface.Normals.Add(normal);
                    min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                    max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                }
            }
        }
    }

    private static Part ReadPart(ReadOnlySpan<byte> bytes)
    {
        Vector3d[]? vertices = null;
        Triangle[]? faces = null;
        string[]? materials = null;
        ReadChunks(bytes, (id, data) =>
        {
            if (id == 0x4110)
            {
                if (vertices is not null)
                    throw new InvalidDataException("Duplicate 3DS vertex list.");
                int count = ReadU16(data);
                if (data.Length != 2 + count * 12)
                    throw new InvalidDataException("Invalid 3DS vertex list length.");
                vertices = new Vector3d[count];
                for (int index = 0; index < count; index++)
                    vertices[index] = ReadVector(data[(2 + index * 12)..]);
            }
            else if (id == 0x4120)
            {
                if (faces is not null)
                    throw new InvalidDataException("Duplicate 3DS face list.");
                int count = ReadU16(data);
                int end = 2 + count * 8;
                if (end > data.Length)
                    throw new InvalidDataException("Truncated 3DS face list.");
                var groups = new string[count];
                faces = new Triangle[count];
                for (int index = 0; index < count; index++)
                {
                    ReadOnlySpan<byte> row = data[(2 + index * 8)..];
                    faces[index] = new(ReadU16(row), ReadU16(row[2..]), ReadU16(row[4..]));
                }
                ReadChunks(data[end..], (faceKind, group) =>
                {
                    if (faceKind == 0x4130)
                        AssignMaterial(group, groups);
                });
                materials = groups;
            }
        });
        return new(vertices ?? throw new InvalidDataException("Missing 3DS vertex list."),
            faces ?? throw new InvalidDataException("Missing 3DS face list."), materials!);
    }

    private static void AssignMaterial(ReadOnlySpan<byte> group, string[] assignments)
    {
        string name = ReadName(group, out int offset);
        ReadOnlySpan<byte> members = group[offset..];
        int count = ReadU16(members);
        if (members.Length != 2 + count * 2)
            throw new InvalidDataException("Invalid 3DS material membership list length.");
        for (int index = 0; index < count; index++)
        {
            int face = ReadU16(members[(2 + index * 2)..]);
            if (face >= assignments.Length || assignments[face] is not null)
                throw new InvalidDataException("Invalid or repeated 3DS material face index.");
            assignments[face] = name;
        }
    }

    private static (string Name, DecodedMaterial Material) ReadMaterial(ReadOnlySpan<byte> data)
    {
        string? name = null;
        Vector3d color = DefaultMaterial.Color;
        double opacity = 1;
        ReadChunks(data, (kind, bytes) =>
        {
            switch (kind)
            {
                case 0xa000:
                    name = ReadName(bytes, out _);
                    break;
                case 0xa020:
                    ReadChunks(bytes, (id, value) =>
                    {
                        if (id is 0x0010 or 0x0013)
                            color = ReadVector(value);
                        else if (id is 0x0011 or 0x0012)
                            color = value.Length == 3 ? new(value[0] / 255.0, value[1] / 255.0, value[2] / 255.0)
                                : throw new InvalidDataException("Invalid 3DS material color.");
                    });
                    break;
                case 0xa050:
                    ReadChunks(bytes, (id, value) =>
                    {
                        if (id == 0x0030)
                            opacity = 1 - ReadU16(value) / 100.0;
                        else if (id == 0x0031 && value.Length == 4)
                            opacity = 1 - BinaryPrimitives.ReadSingleLittleEndian(value);
                    });
                    break;
                case 0xa200:
                    throw new NotSupportedException("3DS texture maps are not supported.");
            }
        });
        if (name is null || color.X is < 0 or > 1 || color.Y is < 0 or > 1 || color.Z is < 0 or > 1 ||
            !double.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new InvalidDataException("Invalid 3DS material.");
        return (name, new(color, opacity, null));
    }

    private static Vector3d ReadVector(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            throw new InvalidDataException("Truncated 3DS vector.");
        Vector3d value = new(BinaryPrimitives.ReadSingleLittleEndian(data),
            BinaryPrimitives.ReadSingleLittleEndian(data[4..]), BinaryPrimitives.ReadSingleLittleEndian(data[8..]));
        return value.IsFinite ? value : throw new InvalidDataException("Non-finite 3DS vector.");
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data) => data.Length >= 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(data)
        : throw new InvalidDataException("Truncated 3DS integer.");

    private static string ReadName(ReadOnlySpan<byte> data, out int offset)
    {
        int end = data.IndexOf((byte)0);
        if (end is < 1 or > 255)
            throw new InvalidDataException("Invalid 3DS name.");
        offset = end + 1;
        return Encoding.Latin1.GetString(data[..end]);
    }

    private static void ReadChunks(ReadOnlySpan<byte> data, ChunkVisitor visit)
    {
        while (!data.IsEmpty)
        {
            if (data.Length < 6)
                throw new InvalidDataException("Truncated 3DS chunk header.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
            if (length < 6 || length > data.Length)
                throw new InvalidDataException("3DS chunk overruns its parent.");
            visit(ReadU16(data), data[6..(int)length]);
            data = data[(int)length..];
        }
    }
}
