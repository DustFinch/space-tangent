// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

/// <summary>Decodes CMOD triangle geometry and local model materials for local body catalogues.</summary>
public static partial class CmodMeshDecoder
{
    private const int MaximumVertices = 1_000_000, MaximumTriangles = 1_000_000, MaximumMaterials = 4096, MaximumSurfaces = 128;
    private static readonly DecodedMaterial Default = new(new(.8, .8, .8), 1, null);

    public static DecodedScienceModel Decode(string root, string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => Read(root, path, nativeBody: false, cancellation));

    public static DecodedScienceModel DecodeNativeBody(string root, string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => Read(root, path, nativeBody: true, cancellation));

    private static DecodedScienceModel Read(string root, string path, bool nativeBody, CancellationToken cancellation)
    {
        using FileStream source = AddonDependencies.OpenRead(path, 32L * 1024 * 1024);
        if (source.Length > 32L * 1024 * 1024)
            throw new InvalidDataException("A CMOD mesh exceeds 32 MiB.");
        Span<byte> header = stackalloc byte[16];
        source.ReadExactly(header);
        bool binary = header.SequenceEqual("#celmodel_binary"u8);
        if (!binary && !header.SequenceEqual("#celmodel__ascii"u8))
            throw new NotSupportedException("The file has no supported CMOD header.");
        using MemoryStream? ascii = binary ? null : ConvertAscii(source, cancellation);
        Stream stream = ascii is null ? source : ascii;
        using var input = new BinaryReader(stream);
        var materials = new List<DecodedMaterial>();
        var surfaces = new Dictionary<DecodedMaterial, Surface>();
        int triangles = 0;
        var min = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var max = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

        while (stream.Position < stream.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            switch (input.ReadInt16())
            {
                case 1001:
                    if (materials.Count >= MaximumMaterials)
                        throw new InvalidDataException("CMOD contains too many materials.");
                    materials.Add(ReadMaterial());
                    break;
                case 1009:
                    ReadMesh();
                    break;
                default:
                    throw new InvalidDataException("Unexpected CMOD block.");
            }
        }

        double diameter = (max - min).Length;
        if (surfaces.Count == 0 || !double.IsFinite(diameter) || diameter <= 0)
            throw new InvalidDataException("CMOD contains no usable triangle geometry.");
        var decoded = new List<DecodedSurface>(surfaces.Count);
        var textures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        long textureBytes = 0;
        foreach (var (material, surface) in surfaces)
        {
            foreach (string name in new[] { material.Texture, material.NormalTexture }.OfType<string>())
            {
                if (textures.ContainsKey(name))
                    continue;
                string texture = SscBodyCatalogue.ResolveTexture(root, path, name);
                using FileStream pixels = AddonDependencies.OpenRead(texture, 16 * 1024 * 1024);
                textureBytes += pixels.Length;
                if (pixels.Length is < 24 or > 16 * 1024 * 1024 || textureBytes > 64 * 1024 * 1024)
                    throw new InvalidDataException("CMOD diffuse textures exceed the supported size.");
                var bytes = new byte[(int)pixels.Length];
                pixels.ReadExactly(bytes);
                ModelTextureFormats.Validate(name, bytes);
                textures.Add(name, bytes);
            }
            decoded.Add(new(surface.Positions.ToArray(), surface.Normals.ToArray(), surface.Uvs.ToArray(), material));
        }
        return new(decoded.AsReadOnly(), textures, diameter);

        DecodedMaterial ReadMaterial()
        {
            DecodedMaterial value = Default;
            for (short token = input.ReadInt16(); token != 1002; token = input.ReadInt16())
            {
                switch (token)
                {
                    case 1003 or 1004 or 1014:
                        Expect(7);
                        Vector3d color = new(Float(), Float(), Float());
                        if (token == 1003)
                            value = value with { Color = color };
                        break;
                    case 1005 or 1006:
                        Expect(1);
                        float scalar = Float();
                        if (token == 1006)
                            value = value with { Opacity = Math.Clamp(scalar, 0, 1) };
                        break;
                    case 1015:
                        input.ReadInt16();
                        break;
                    case 1007:
                        short channel = input.ReadInt16();
                        Expect(5);
                        int length = input.ReadUInt16();
                        if (length is 0 or > 1024 || length > stream.Length - stream.Position)
                            throw new InvalidDataException("Invalid CMOD texture name.");
                        string texture = new UTF8Encoding(false, true).GetString(input.ReadBytes(length));
                        if (channel == 0)
                            value = value with { Texture = texture };
                        else if (channel == 1 && nativeBody)
                            value = value with { NormalTexture = texture };
                        break;
                    default:
                        throw new InvalidDataException("Unexpected CMOD material field.");
                }
            }
            return value;
        }

        void ReadMesh()
        {
            Expect(1011);
            var attributes = new List<(int Semantic, int Format)>();
            for (short semantic = input.ReadInt16(); semantic != 1012; semantic = input.ReadInt16())
            {
                short format = input.ReadInt16();
                if (semantic is < 0 or > 11 || format is < 0 or > 4 ||
                    attributes.Any(attribute => attribute.Semantic == semantic))
                    throw new InvalidDataException("Invalid CMOD vertex attribute.");
                attributes.Add((semantic, format));
            }
            if (!attributes.Contains((0, 2)))
                throw new InvalidDataException("CMOD vertices must contain three-component positions.");
            Expect(1013);
            uint count = input.ReadUInt32();
            int stride = attributes.Sum(item => item.Format == 4 ? 4 : (item.Format + 1) * 4);
            if (count is 0 or > MaximumVertices || (long)stride * count > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid CMOD vertex count.");
            var vertices = new Vertex[count];
            Span<float> components = stackalloc float[4];
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                if ((vertex & 8191) == 0)
                    cancellation.ThrowIfCancellationRequested();
                foreach (var (semantic, format) in attributes)
                {
                    if (format == 4)
                    {
                        input.ReadUInt32();
                        continue;
                    }
                    components.Clear();
                    for (int i = 0; i <= format; i++)
                        components[i] = Float();
                    if (semantic == 0)
                        vertices[vertex].Position = new(components[0], components[1], components[2]);
                    else if (semantic == 3)
                        vertices[vertex].Normal = new(components[0], components[1], components[2]);
                    else if (semantic == 5)
                        (vertices[vertex].U, vertices[vertex].V) = (components[0], nativeBody ? components[1] : 1 - components[1]);
                }
            }
            for (short type = input.ReadInt16(); type != 1010; type = input.ReadInt16())
            {
                uint material = input.ReadUInt32(), indexCount = input.ReadUInt32();
                if (type is < 0 or > 6 || indexCount > 3 * MaximumTriangles ||
                    (long)indexCount * 4 > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid CMOD primitive group.");
                if (type > 2)
                {
                    stream.Seek(indexCount * 4L, SeekOrigin.Current);
                    continue;
                }
                if (indexCount < 3 || (type == 0 && indexCount % 3 != 0))
                    throw new InvalidDataException("Incomplete CMOD triangle group.");
                DecodedMaterial definition = material == uint.MaxValue ? Default : material < materials.Count
                    ? materials[(int)material] : throw new InvalidDataException("A CMOD surface refers to a missing material.");
                if (!surfaces.TryGetValue(definition, out Surface? surface))
                {
                    if (surfaces.Count >= MaximumSurfaces)
                        throw new InvalidDataException("CMOD contains too many material groups.");
                    surfaces.Add(definition, surface = new());
                }
                uint first = Index(), previous = Index();
                for (uint i = 2; i < indexCount; i++)
                {
                    if ((i & 8191) == 0)
                        cancellation.ThrowIfCancellationRequested();
                    uint next = Index();
                    if (++triangles > MaximumTriangles)
                        throw new InvalidDataException("CMOD contains too many triangles.");
                    uint a = first, b = previous;
                    if (type == 1 && (i & 1) == 1)
                        (a, b) = (b, a);
                    AddTriangle(surface, vertices[a], vertices[b], vertices[next]);
                    if (type == 0 && i + 2 < indexCount)
                    {
                        first = Index();
                        previous = Index();
                        i += 2;
                    }
                    else if (type == 1)
                    {
                        first = previous;
                        previous = next;
                    }
                    else if (type == 2)
                        previous = next;
                }
            }

            uint Index()
            {
                uint index = input.ReadUInt32();
                return index < count ? index : throw new InvalidDataException("CMOD refers to an unavailable vertex.");
            }
        }

        void AddTriangle(Surface surface, Vertex a, Vertex b, Vertex c)
        {
            Vector3d cross = Vector3d.Cross(b.Position - a.Position, c.Position - a.Position);
            double length = cross.Length;
            if (length == 0)
                return;
            Vector3d fallback = cross / length;
            Append(a);
            Append(nativeBody ? c : b);
            Append(nativeBody ? b : c);

            void Append(Vertex vertex)
            {
                var point = vertex.Position;
                surface.Positions.Add(point);
                surface.Normals.Add(vertex.Normal.Length > 0 ? vertex.Normal : fallback);
                surface.Uvs.Add(vertex.U);
                surface.Uvs.Add(vertex.V);
                min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
            }
        }

        void Expect(int token)
        {
            if (input.ReadInt16() != token)
                throw new InvalidDataException("Unexpected CMOD field.");
        }

        float Float()
        {
            float number = input.ReadSingle();
            return float.IsFinite(number) ? number : throw new InvalidDataException("CMOD contains a nonfinite number.");
        }
    }

    private struct Vertex
    {
        internal Vector3d Position, Normal;
        internal float U, V;
    }

    private sealed class Surface
    {
        internal readonly List<Vector3d> Positions = [], Normals = [];
        internal readonly List<float> Uvs = [];
    }
}
