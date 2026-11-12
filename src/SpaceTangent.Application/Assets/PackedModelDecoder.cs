// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

/// <summary>Decodes the embedded triangular model written by the legacy pack tool.</summary>
public static class PackedModelDecoder
{
    private const uint Magic = 0x46546c67, JsonChunk = 0x4e4f534a, BinaryChunk = 0x004e4942;
    private const string DdsExtension = "MSFT_texture_dds";
    private const int MaximumModelBytes = 64 * 1024 * 1024, MaximumCorners = 3_000_000, MaximumSurfaces = 128;
    private static readonly DecodedMaterial White = new(new(1, 1, 1), 1, null);

    public static DecodedScienceModel Decode(string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => Read(path, cancellation));

    private static DecodedScienceModel Read(string path, CancellationToken cancellation)
    {
        using FileStream stream = AddonDependencies.OpenRead(path, MaximumModelBytes);
        if (stream.Length is < 28 or > MaximumModelBytes)
            throw new InvalidDataException("Invalid packed model length.");
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (Word(header) != Magic || Word(header[4..]) != 2 || Word(header[8..]) != stream.Length)
            throw new InvalidDataException("Expected a complete GLB 2 model.");
        byte[] json = Chunk(JsonChunk);
        byte[] binary = Chunk(BinaryChunk);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing chunks in the packed model.");
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        JsonElement meshes = root.GetProperty("meshes");
        JsonElement nodes = root.GetProperty("nodes");
        JsonElement buffers = root.GetProperty("buffers");
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0" ||
            root.TryGetProperty("extensionsRequired", out var required) &&
            required.EnumerateArray().Any(extension => extension.GetString() != DdsExtension))
            throw new NotSupportedException("This packed model requires a different GLB profile.");
        if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _))
            throw new InvalidDataException("Packed models require a single embedded buffer.");
        int declaredLength = buffers[0].GetProperty("byteLength").GetInt32();
        if (declaredLength < 0 || declaredLength > binary.Length || binary.Length - declaredLength > 3)
            throw new InvalidDataException("The packed buffer length does not match its data.");
        if (nodes.GetArrayLength() != 1 || nodes[0].EnumerateObject().Any(field => field.Name is not ("mesh" or "name")) ||
            meshes.GetArrayLength() != 1 || nodes[0].GetProperty("mesh").GetInt32() != 0)
            throw new NotSupportedException("Packed model meshes must be flattened by the pack tool.");
        JsonElement primitives = meshes[0].GetProperty("primitives");
        if (primitives.GetArrayLength() is < 1 or > MaximumSurfaces)
            throw new InvalidDataException("Invalid packed model surface count.");
        JsonElement views = root.GetProperty("bufferViews"), accessors = root.GetProperty("accessors");
        var textures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var surfaces = new List<DecodedSurface>();
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        int corners = 0;
        foreach (var primitive in primitives.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            if (Int(primitive, "mode", 4) != 4)
                throw new NotSupportedException("Packed models must contain triangle primitives.");
            var fields = primitive.GetProperty("attributes");
            var position = Access(fields.GetProperty("POSITION").GetInt32(), "VEC3", 5126, 12);
            var normal = Access(fields.GetProperty("NORMAL").GetInt32(), "VEC3", 5126, 12);
            var uv = Access(fields.GetProperty("TEXCOORD_0").GetInt32(), "VEC2", 5126, 8);
            int index = primitive.GetProperty("indices").GetInt32();
            int indexType = accessors[index].GetProperty("componentType").GetInt32();
            int indexSize = indexType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("Invalid packed index type.") };
            var indices = Access(index, "SCALAR", indexType, indexSize);
            if (position.Count != normal.Count || position.Count != uv.Count || indices.Count % 3 != 0 ||
                indices.Count > MaximumCorners - corners)
                throw new InvalidDataException("Invalid packed primitive counts.");
            corners += indices.Count;

            var vertices = new Vector3d[indices.Count];
            var normals = new Vector3d[indices.Count];
            var coordinates = new float[indices.Count * 2];
            for (int at = 0; at < indices.Count; at++)
            {
                if ((at & 4095) == 0)
                    cancellation.ThrowIfCancellationRequested();
                int input = (at % 3) switch { 1 => at + 1, 2 => at - 1, _ => at };
                var bytes = binary.AsSpan(indices.Offset + input * indices.Stride, indexSize);
                uint vertex = indexSize switch { 1 => bytes[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes), _ => Word(bytes) };
                if (vertex >= position.Count)
                    throw new InvalidDataException("A packed triangle index is outside its vertex range.");
                Vector3d p = vertices[at] = Vector(position, (int)vertex);
                normals[at] = Vector(normal, (int)vertex);
                coordinates[at * 2] = Float(uv, (int)vertex, 0);
                coordinates[at * 2 + 1] = Float(uv, (int)vertex, 1);
                minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
                maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
            }
            var material = primitive.TryGetProperty("material", out var selected) ? Material(selected.GetInt32()) : White;
            surfaces.Add(new(vertices, normals, coordinates, material));
        }
        double diameter = (maximum - minimum).Length;
        if (!(diameter > 0) || !double.IsFinite(diameter))
            throw new InvalidDataException("Packed model has invalid bounds.");
        return new(surfaces.AsReadOnly(), new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(textures), diameter);

        byte[] Chunk(uint kind)
        {
            Span<byte> info = stackalloc byte[8];
            stream.ReadExactly(info);
            uint length = Word(info);
            if (Word(info[4..]) != kind || length == 0 || length % 4 != 0 || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid packed model chunk.");
            var data = new byte[(int)length];
            stream.ReadExactly(data);
            return data;
        }

        (int Offset, int Length, int Stride) View(int index)
        {
            JsonElement view = views[index];
            int offset = Int(view, "byteOffset", 0), length = view.GetProperty("byteLength").GetInt32();
            if (view.GetProperty("buffer").GetInt32() != 0 || offset < 0 || length < 0 || (long)offset + length > declaredLength)
                throw new InvalidDataException("Packed buffer view is outside the embedded data.");
            return (offset, length, Int(view, "byteStride", 0));
        }

        (int Offset, int Count, int Stride) Access(int index, string shape, int type, int width)
        {
            JsonElement accessor = accessors[index];
            if (accessor.GetProperty("type").GetString() != shape || accessor.GetProperty("componentType").GetInt32() != type ||
                accessor.TryGetProperty("sparse", out _) || accessor.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean())
                throw new NotSupportedException("Unexpected packed accessor profile.");
            var view = View(accessor.GetProperty("bufferView").GetInt32());
            int offset = Int(accessor, "byteOffset", 0), count = accessor.GetProperty("count").GetInt32();
            int stride = view.Stride == 0 ? width : view.Stride;
            if (count <= 0 || offset < 0 || stride < width || (long)offset + (long)(count - 1) * stride + width > view.Length)
                throw new InvalidDataException("Packed accessor is outside its buffer view.");
            return (view.Offset + offset, count, stride);
        }

        float Float((int Offset, int Count, int Stride) accessor, int index, int axis)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(binary.AsSpan(accessor.Offset + index * accessor.Stride + axis * 4, 4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("A packed vertex coordinate is not finite.");
        }

        Vector3d Vector((int Offset, int Count, int Stride) accessor, int index) =>
            new(Float(accessor, index, 0), Float(accessor, index, 1), Float(accessor, index, 2));

        DecodedMaterial Material(int index)
        {
            var value = root.GetProperty("materials")[index];
            if (!value.TryGetProperty("pbrMetallicRoughness", out var pbr))
                return White;
            double[] color = pbr.TryGetProperty("baseColorFactor", out var colorValue)
                ? colorValue.EnumerateArray().Select(channel => channel.GetDouble()).ToArray() : [1, 1, 1, 1];
            if (color.Length != 4 || color.Any(channel => !double.IsFinite(channel) || channel is < 0 or > 1))
                throw new InvalidDataException("Invalid packed material base color.");
            string? texture = null;
            if (pbr.TryGetProperty("baseColorTexture", out var tex))
            {
                int textureIndex = tex.GetProperty("index").GetInt32();
                var textureSpec = root.GetProperty("textures")[textureIndex];
                int imageIndex = textureSpec.TryGetProperty("extensions", out var extensions) &&
                    extensions.TryGetProperty(DdsExtension, out var dds)
                        ? dds.GetProperty("source").GetInt32() : textureSpec.GetProperty("source").GetInt32();
                var image = root.GetProperty("images")[imageIndex];
                string extension = image.GetProperty("mimeType").GetString() switch
                {
                    "image/png" => ".png",
                    "image/vnd-ms.dds" => ".dds",
                    _ => throw new NotSupportedException("Packed textures require embedded PNG or DDS data.")
                };
                if (image.TryGetProperty("uri", out _))
                    throw new NotSupportedException("Packed textures must be embedded.");
                texture = FormattableString.Invariant($"embedded-{imageIndex}{extension}");
                if (!textures.ContainsKey(texture))
                {
                    var view = View(image.GetProperty("bufferView").GetInt32());
                    var data = binary.AsMemory(view.Offset, view.Length);
                    ModelTextureFormats.Validate(texture, data.Span);
                    textures.Add(texture, data);
                }
            }
            return new(new(Srgb(color[0]), Srgb(color[1]), Srgb(color[2])), color[3], texture);
        }
    }

    private static double Srgb(double linear) => linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
    private static uint Word(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static int Int(JsonElement node, string field, int fallback) =>
        node.TryGetProperty(field, out var value) ? value.GetInt32() : fallback;
}
