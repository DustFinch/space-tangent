// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

/// <summary>Decodes static glTF scenes and the embedded GLB models written by the pack tool.</summary>
public static partial class PackedModelDecoder
{
    private const uint Magic = 0x46546c67, JsonChunk = 0x4e4f534a, BinaryChunk = 0x004e4942;
    private const string DdsExtension = "MSFT_texture_dds";
    private const string EmissiveExtension = "KHR_materials_emissive_strength";
    private const int MaximumModelBytes = 64 * 1024 * 1024, MaximumCorners = 3_000_000, MaximumSurfaces = 128;
    private static readonly DecodedMaterial White = new(new(1, 1, 1), 1, null);

    public static DecodedScienceModel Decode(string path, CancellationToken cancellation = default) =>
        Decode(Path.GetDirectoryName(Path.GetFullPath(path))!, path, cancellation);

    public static DecodedScienceModel Decode(string root, string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => Read(root, path, cancellation));

    private static DecodedScienceModel Read(string addOnRoot, string path, CancellationToken cancellation)
    {
        using FileStream stream = AddonDependencies.OpenRead(path, MaximumModelBytes);
        if (Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
        {
            using var text = JsonDocument.Parse(stream);
            return ReadScene(text.RootElement, null, addOnRoot, path, cancellation);
        }
        if (stream.Length is < 20 or > MaximumModelBytes)
            throw new InvalidDataException("Invalid GLB model length.");
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (Word(header) != Magic || Word(header[4..]) != 2 || Word(header[8..]) != stream.Length)
            throw new InvalidDataException("Expected a complete GLB 2 model.");
        byte[] json = Chunk(JsonChunk);
        byte[]? binary = stream.Position < stream.Length ? Chunk(BinaryChunk) : null;
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing chunks in the GLB model.");
        using var doc = JsonDocument.Parse(json);
        return ReadScene(doc.RootElement, binary, addOnRoot, path, cancellation);

        byte[] Chunk(uint kind)
        {
            Span<byte> info = stackalloc byte[8];
            stream.ReadExactly(info);
            uint length = Word(info);
            if (Word(info[4..]) != kind || length == 0 || length % 4 != 0 || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid GLB model chunk.");
            var data = new byte[(int)length];
            stream.ReadExactly(data);
            return data;
        }
    }

    private static DecodedScienceModel ReadScene(JsonElement root, byte[]? binary, string addOnRoot, string path, CancellationToken cancellation)
    {
        JsonElement meshes = root.GetProperty("meshes");
        JsonElement buffers = root.GetProperty("buffers");
        if (root.GetProperty("asset").GetProperty("version").GetString() != "2.0" ||
            root.TryGetProperty("extensionsRequired", out var required) &&
            required.EnumerateArray().Any(extension => extension.GetString() is not (DdsExtension or EmissiveExtension)))
            throw new NotSupportedException("This glTF model uses an unsupported version or required extension.");
        int budget = MaximumModelBytes;
        var dataBuffers = new List<ReadOnlyMemory<byte>>();
        foreach (var buffer in buffers.EnumerateArray())
        {
            int declaredLength = buffer.GetProperty("byteLength").GetInt32();
            if (declaredLength < 0 || declaredLength > budget || dataBuffers.Count >= 128)
                throw new InvalidDataException("glTF buffers exceed the model budget.");
            var data = buffer.TryGetProperty("uri", out var uri)
                ? ReadUri(uri.GetString()!, addOnRoot, path, declaredLength)
                : dataBuffers.Count == 0 && binary is not null ? binary :
                    throw new InvalidDataException("glTF buffer is missing its binary chunk.");
            if (data.Length < declaredLength || data.Length - declaredLength > 3)
                throw new InvalidDataException("A glTF buffer length does not match its data.");
            dataBuffers.Add(data.AsMemory(0, declaredLength));
            budget -= declaredLength;
        }
        JsonElement views = root.GetProperty("bufferViews"), accessors = root.GetProperty("accessors");
        var textures = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var surfaces = new List<DecodedSurface>();
        var minimum = new Vector3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = new Vector3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        int corners = 0;
        foreach (var (mesh, transform) in SceneMeshes(root, cancellation))
        foreach (var primitive in meshes[mesh].GetProperty("primitives").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            int mode = Int(primitive, "mode", 4);
            if (mode is not (4 or 5 or 6))
                continue;
            if (surfaces.Count >= MaximumSurfaces || !Matrix4x4.Invert(transform, out var inverse))
                throw new InvalidDataException("glTF exceeds the surface budget or contains a singular transform.");
            var normalMatrix = Matrix4x4.Transpose(inverse);
            bool mirrored = transform.GetDeterminant() < 0;
            var fields = primitive.GetProperty("attributes");
            var position = Access(fields.GetProperty("POSITION").GetInt32(), "VEC3", 5126, 12);
            var normal = fields.TryGetProperty("NORMAL", out var n) ? Access(n.GetInt32(), "VEC3", 5126, 12) : default;
            var uv = fields.TryGetProperty("TEXCOORD_0", out var t) ? Access(t.GetInt32(), "VEC2", 5126, 8) : default;
            var indices = default(ModelAccessor);
            int indexSize = 0;
            if (primitive.TryGetProperty("indices", out var index))
            {
                int indexType = accessors[index.GetInt32()].GetProperty("componentType").GetInt32();
                indexSize = indexType switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("Invalid glTF index type.") };
                indices = Access(index.GetInt32(), "SCALAR", indexType, indexSize);
            }
            int points = indexSize == 0 ? position.Count : indices.Count;
            long count = mode == 4 ? points : Math.Max(0, points - 2) * 3L;
            if (normal.Count != 0 && position.Count != normal.Count || uv.Count != 0 && position.Count != uv.Count ||
                count % 3 != 0 || count < 3 || count > MaximumCorners - corners)
                throw new InvalidDataException("Invalid glTF primitive counts.");
            int triangleCorners = (int)count;
            corners += triangleCorners;

            var vertices = new Vector3d[triangleCorners];
            var normals = new Vector3d[triangleCorners];
            var coordinates = new float[triangleCorners * 2];
            for (int at = 0; at < triangleCorners; at++)
            {
                if ((at & 4095) == 0)
                    cancellation.ThrowIfCancellationRequested();
                int triangle = at / 3, corner = at % 3;
                if (!mirrored && corner != 0)
                    corner = 3 - corner;
                int input = mode switch
                {
                    5 => triangle + (triangle % 2 != 0 && corner < 2 ? 1 - corner : corner),
                    6 => corner == 0 ? 0 : triangle + corner,
                    _ => triangle * 3 + corner
                };
                uint vertex = (uint)input;
                if (indexSize != 0)
                {
                    var bytes = indices.Data.Span.Slice(indices.Offset + input * indices.Stride, indexSize);
                    vertex = indexSize switch { 1 => bytes[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes), _ => Word(bytes) };
                }
                if (vertex >= position.Count)
                    throw new InvalidDataException("A packed triangle index is outside its vertex range.");
                Vector3d p = vertices[at] = Checked(Vector3.Transform(Vector(position, (int)vertex), transform));
                if (normal.Count != 0)
                {
                    var direction = Checked(Vector3.TransformNormal(Vector(normal, (int)vertex), normalMatrix));
                    normals[at] = direction.Length > 0 ? direction / direction.Length : default;
                }
                if (uv.Count != 0)
                {
                    coordinates[at * 2] = Float(uv, (int)vertex, 0);
                    coordinates[at * 2 + 1] = Float(uv, (int)vertex, 1);
                }
                minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
                maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
            }
            for (int i = 0; i < triangleCorners; i += 3)
            {
                if (normals[i].Length > 0 && normals[i + 1].Length > 0 && normals[i + 2].Length > 0)
                    continue;
                var face = Vector3d.Cross(vertices[i + 2] - vertices[i], vertices[i + 1] - vertices[i]);

                face = face.Length > 0 ? face / face.Length : new(0, 1, 0);
                for (int corner = i; corner < i + 3; corner++)
                    if (normals[corner].Length == 0)
                        normals[corner] = face;
            }
            var material = primitive.TryGetProperty("material", out var selected) ? Material(selected.GetInt32()) : White;
            surfaces.Add(new(vertices, normals, coordinates, material));
        }
        double diameter = (maximum - minimum).Length;
        if (!(diameter > 0) || !double.IsFinite(diameter))
            throw new InvalidDataException("Packed model has invalid bounds.");
        return new(surfaces.AsReadOnly(), new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(textures), diameter);

        (ReadOnlyMemory<byte> Data, int Stride) View(int index)
        {
            JsonElement view = views[index];
            int offset = Int(view, "byteOffset", 0), length = view.GetProperty("byteLength").GetInt32();
            var data = dataBuffers[view.GetProperty("buffer").GetInt32()];
            if (offset < 0 || length < 0 || (long)offset + length > data.Length)
                throw new InvalidDataException("glTF buffer view is outside its data.");
            return (data.Slice(offset, length), Int(view, "byteStride", 0));
        }

        ModelAccessor Access(int index, string shape, int type, int width)
        {
            JsonElement accessor = accessors[index];
            if (accessor.GetProperty("type").GetString() != shape || accessor.GetProperty("componentType").GetInt32() != type ||
                accessor.TryGetProperty("sparse", out _) || accessor.TryGetProperty("normalized", out var normalized) && normalized.GetBoolean())
                throw new NotSupportedException("Unexpected packed accessor profile.");
            var view = View(accessor.GetProperty("bufferView").GetInt32());
            int offset = Int(accessor, "byteOffset", 0), count = accessor.GetProperty("count").GetInt32();
            int stride = view.Stride == 0 ? width : view.Stride;
            if (count <= 0 || offset < 0 || stride < width || (long)offset + (long)(count - 1) * stride + width > view.Data.Length)
                throw new InvalidDataException("Packed accessor is outside its buffer view.");
            return new(view.Data, offset, count, stride);
        }

        float Float(ModelAccessor accessor, int index, int axis)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(accessor.Data.Span.Slice(accessor.Offset + index * accessor.Stride + axis * 4, 4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("A packed vertex coordinate is not finite.");
        }

        Vector3 Vector(ModelAccessor accessor, int index) =>
            new(Float(accessor, index, 0), Float(accessor, index, 1), Float(accessor, index, 2));

        DecodedMaterial Material(int index)
        {
            var value = root.GetProperty("materials")[index];
            var pbr = value.TryGetProperty("pbrMetallicRoughness", out var metallic) ? metallic : default;
            double[] color = Channels(pbr, "baseColorFactor", [1, 1, 1, 1]);
            double[] emission = Channels(value, "emissiveFactor", [0, 0, 0]);
            double strength = value.TryGetProperty("extensions", out var extensions) &&
                extensions.TryGetProperty(EmissiveExtension, out var emissive)
                ? emissive.GetProperty("emissiveStrength").GetDouble() : 1;
            if (!double.IsFinite(strength) || strength is < 0 or > 100_000)
                throw new InvalidDataException("Invalid glTF emission strength.");
            string? alphaMode = value.TryGetProperty("alphaMode", out var alpha) ? alpha.GetString() : null;
            double cutoff = value.TryGetProperty("alphaCutoff", out var threshold) ? threshold.GetDouble() : .5;
            if (alphaMode is not (null or "OPAQUE" or "BLEND" or "MASK") ||
                !double.IsFinite(cutoff) || cutoff is < 0 or > 1)
                throw new InvalidDataException("Invalid glTF alpha settings.");
            double opacity = alphaMode is "BLEND" or "MASK" ? color[3] : 1;
            return new(new(Srgb(color[0]), Srgb(color[1]), Srgb(color[2])), opacity, Texture(pbr, "baseColorTexture"))
            {
                NormalTexture = Texture(value, "normalTexture"),
                EmissionTexture = Texture(value, "emissiveTexture"),
                Emission = new(emission[0] * strength, emission[1] * strength, emission[2] * strength),
                AlphaBlend = alphaMode == "BLEND",
                AlphaCutoff = alphaMode == "MASK" ? cutoff : null
            };
        }

        static double[] Channels(JsonElement owner, string property, double[] fallback)
        {
            if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var raw))
                return fallback;
            double[] channels = raw.EnumerateArray().Select(channel => channel.GetDouble()).ToArray();
            if (channels.Length != fallback.Length || channels.Any(channel => !double.IsFinite(channel) || channel is < 0 or > 1))
                throw new InvalidDataException("Invalid glTF material color.");
            return channels;
        }

        string? Texture(JsonElement owner, string property)
        {
            if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var tex))
                return null;
            if (Int(tex, "texCoord", 0) != 0)
                throw new NotSupportedException("The model uses an additional texture coordinate set.");
            int textureIndex = tex.GetProperty("index").GetInt32();
            var textureSpec = root.GetProperty("textures")[textureIndex];
            int imageIndex = textureSpec.TryGetProperty("extensions", out var extensions) &&
                extensions.TryGetProperty(DdsExtension, out var dds)
                    ? dds.GetProperty("source").GetInt32() : textureSpec.GetProperty("source").GetInt32();
            var image = root.GetProperty("images")[imageIndex];
            string? uri = image.TryGetProperty("uri", out var name) ? name.GetString() : null;
            string extension = ImageExtension(image, uri);
            string texture = FormattableString.Invariant($"embedded-{imageIndex}{extension}");
            if (!textures.ContainsKey(texture))
            {
                ReadOnlyMemory<byte> data = uri is null ? View(image.GetProperty("bufferView").GetInt32()).Data :
                    ReadUri(uri, addOnRoot, path, Math.Min(budget, 16 * 1024 * 1024));
                ModelTextureFormats.Validate(texture, data.Span);
                textures.Add(texture, data);
                if (uri is not null)
                    budget -= data.Length;
            }
            return texture;
        }
    }

    private static double Srgb(double linear) => linear <= .0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
    private static uint Word(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);
    private static int Int(JsonElement node, string field, int fallback) =>
        node.TryGetProperty(field, out var value) ? value.GetInt32() : fallback;
}
