// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace SpaceTangent.Application.Assets;

internal static class ModelTextureFormats
{
    internal static bool IsDds(string file) => Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase);

    internal static void Validate(string file, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("A model texture exceeds 16 MiB.");
        if (IsDds(file))
        {
            if (bytes.Length < 128 || !bytes.StartsWith("DDS "u8) ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 124 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..]) != 32 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) is 0 or > 8192 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) is 0 or > 8192 ||
                !(bytes.Slice(84, 4).SequenceEqual("DXT1"u8) || bytes.Slice(84, 4).SequenceEqual("DXT3"u8) ||
                    bytes.Slice(84, 4).SequenceEqual("DXT5"u8)))
                throw new InvalidDataException("A model DDS texture requires bounded BC1, BC2 or BC3 data.");
            return;
        }
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.StartsWith(png) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) is 0 or > 8192 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) is 0 or > 8192)
            throw new InvalidDataException("A model PNG texture has invalid dimensions or signature.");
    }
}
