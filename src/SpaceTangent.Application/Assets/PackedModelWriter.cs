// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application.Assets;

/// <summary>Writes a static triangle model and its material images as a single GLB.</summary>
public static class PackedModelWriter
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private const string DdsExtension = "MSFT_texture_dds";
    private const string EmissiveExtension = "KHR_materials_emissive_strength";

    public static void Write(DecodedScienceModel model, Stream output)
    {
        if (model.Sprites.Count > 0)
            throw new NotSupportedException("GLB cannot preserve this model's camera-facing sprites.");
        if (model.Surfaces.Count is 0 or > 128)
            throw new InvalidDataException("GLB export requires one to 128 triangle surfaces.");

        using var binary = new MemoryStream();
        using var data = new BinaryWriter(binary);
        var views = new JsonArray();
        var accessors = new JsonArray();
        var primitives = new JsonArray();
        var materials = new JsonArray();
        var images = new JsonArray();
        var textures = new JsonArray();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var required = new SortedSet<string>(StringComparer.Ordinal);
        var textureIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var surface in model.Surfaces)
        {
            int count = surface.Positions.Length;
            if (count < 3 || count % 3 != 0 || count != surface.Normals.Length ||
                count * 2L != surface.TextureCoordinates.Length || binary.Length + count * 32L > MaximumBytes)
                throw new InvalidDataException("Invalid triangle data or GLB export exceeds 64 MiB.");

            var attributes = new JsonObject
            {
                ["POSITION"] = WriteVectors(surface.Positions.Span, bounds: true),
                ["NORMAL"] = WriteVectors(surface.Normals.Span, bounds: false)
            };
            int uvStart = (int)binary.Position;
            var uv = surface.TextureCoordinates.Span;
            for (int index = 0; index < count; index++)
            {
                int corner = SourceCorner(index) * 2;
                data.Write(Finite(uv[corner]));
                data.Write(Finite(uv[corner + 1]));
            }
            attributes["TEXCOORD_0"] = AddAccessor(uvStart, count, "VEC2");

            primitives.Add(new JsonObject { ["attributes"] = attributes, ["material"] = materials.Count });
            materials.Add(Material(surface.Material));
        }

        int unpaddedBinaryLength = (int)binary.Length;
        Align(data, binary.Length, 0);
        var document = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "SpaceTangent" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject { ["primitives"] = primitives }),
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = unpaddedBinaryLength }),
            ["materials"] = materials
        };
        if (images.Count > 0)
        {
            document["images"] = images;
            document["textures"] = textures;
        }
        if (used.Count > 0)
            document["extensionsUsed"] = JsonSerializer.SerializeToNode(used);
        if (required.Count > 0)
            document["extensionsRequired"] = JsonSerializer.SerializeToNode(required);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document);
        int jsonLength = (json.Length + 3) & ~3;
        long length = 28L + jsonLength + binary.Length;
        if (length > MaximumBytes)
            throw new InvalidDataException("GLB export exceeds 64 MiB.");
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46546c67u);
        writer.Write(2u);
        writer.Write((uint)length);
        writer.Write(jsonLength);
        writer.Write(0x4e4f534au);
        writer.Write(json);
        Align(writer, json.Length, 0x20);
        writer.Write((int)binary.Length);
        writer.Write(0x004e4942u);
        binary.WriteTo(output);

        int WriteVectors(ReadOnlySpan<Vector3d> vectors, bool bounds)
        {
            int start = (int)binary.Position;
            float[] minimum = [float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
            float[] maximum = [float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity];
            for (int index = 0; index < vectors.Length; index++)
            {
                Vector3d value = vectors[SourceCorner(index)];
                if (!bounds)
                    value = value.Length > 0 ? value / value.Length : new(0, 1, 0);
                WriteComponent(value.X, 0);
                WriteComponent(value.Y, 1);
                WriteComponent(value.Z, 2);
            }
            int accessor = AddAccessor(start, vectors.Length, "VEC3");
            if (bounds)
            {
                accessors[accessor]!["min"] = JsonSerializer.SerializeToNode(minimum);
                accessors[accessor]!["max"] = JsonSerializer.SerializeToNode(maximum);
            }
            return accessor;

            void WriteComponent(double component, int axis)
            {
                float value = Finite(component);
                data.Write(value);
                if (!bounds)
                    return;
                minimum[axis] = Math.Min(minimum[axis], value);
                maximum[axis] = Math.Max(maximum[axis], value);
            }
        }

        int AddAccessor(int start, int count, string type)
        {
            int index = accessors.Count;
            accessors.Add(new JsonObject
            {
                ["bufferView"] = AddView(start, (int)binary.Position - start),
                ["componentType"] = 5126, ["count"] = count, ["type"] = type
            });
            return index;
        }

        int AddView(int start, int length)
        {
            int index = views.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = start, ["byteLength"] = length });
            return index;
        }

        JsonObject Material(DecodedMaterial material)
        {
            var pbr = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(Linear(material.Color.X), Linear(material.Color.Y),
                    Linear(material.Color.Z), material.Opacity),
                ["metallicFactor"] = 0,
                ["roughnessFactor"] = 1
            };
            var result = new JsonObject { ["pbrMetallicRoughness"] = pbr };
            if (material.AlphaCutoff is { } cutoff)
            {
                result["alphaMode"] = "MASK";
                result["alphaCutoff"] = cutoff;
            }
            else if (material.AlphaBlend || material.Opacity < 1)
                result["alphaMode"] = "BLEND";

            double strength = Math.Max(1, Math.Max(material.Emission.X, Math.Max(material.Emission.Y, material.Emission.Z)));
            result["emissiveFactor"] = new JsonArray(material.Emission.X / strength, material.Emission.Y / strength,
                material.Emission.Z / strength);
            if (strength > 1)
            {
                used.Add(EmissiveExtension);
                result["extensions"] = new JsonObject { [EmissiveExtension] = new JsonObject { ["emissiveStrength"] = strength } };
            }
            AttachTexture(pbr, "baseColorTexture", material.Texture);
            AttachTexture(result, "normalTexture", material.NormalTexture);
            AttachTexture(result, "emissiveTexture", material.EmissionTexture);
            return result;
        }

        void AttachTexture(JsonObject owner, string property, string? name)
        {
            if (name is null)
                return;
            if (!textureIds.TryGetValue(name, out int texture))
            {
                if (!model.Textures.TryGetValue(name, out var bytes))
                    throw new InvalidDataException($"Model texture is missing: {name}.");
                string mime = Path.GetExtension(name).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".dds" => "image/vnd-ms.dds",
                    _ => throw new NotSupportedException($"Unsupported model texture: {name}.")
                };
                ModelTextureFormats.Validate(name, bytes.Span);
                Align(data, binary.Length, 0);
                int start = (int)binary.Position;
                if (start + (long)bytes.Length > MaximumBytes)
                    throw new InvalidDataException("GLB export exceeds 64 MiB.");
                data.Write(bytes.Span);
                int image = images.Count;
                images.Add(new JsonObject { ["bufferView"] = AddView(start, bytes.Length), ["mimeType"] = mime });
                texture = textures.Count;
                if (ModelTextureFormats.IsDds(name))
                {
                    used.Add(DdsExtension);
                    required.Add(DdsExtension);
                    textures.Add(new JsonObject { ["extensions"] = new JsonObject { [DdsExtension] = new JsonObject { ["source"] = image } } });
                }
                else
                    textures.Add(new JsonObject { ["source"] = image });
                textureIds.Add(name, texture);
            }
            owner[property] = new JsonObject { ["index"] = texture };
        }
    }

    // The in-memory surfaces use clockwise triangles; glTF uses counterclockwise triangles.
    private static int SourceCorner(int index) => (index % 3) switch { 1 => index + 1, 2 => index - 1, _ => index };

    private static float Finite(double value) => float.IsFinite((float)value) ? (float)value :
        throw new InvalidDataException("Model coordinates must fit a finite GLB float.");

    private static double Linear(double srgb) => srgb <= .04045 ? srgb / 12.92 : Math.Pow((srgb + .055) / 1.055, 2.4);

    private static void Align(BinaryWriter writer, long length, byte padding)
    {
        for (long index = length; index % 4 != 0; index++)
            writer.Write(padding);
    }
}
