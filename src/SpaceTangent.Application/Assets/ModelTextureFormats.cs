// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace SpaceTangent.Application.Assets;

public static class ModelTextureFormats
{
    internal static bool IsDds(string file) => Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase);

    public static void Validate(string file, ReadOnlySpan<byte> bytes)
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
        if (Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg")
        {
            if (!ValidJpeg(bytes))
                throw new InvalidDataException("A model JPEG texture has invalid dimensions or signature.");
            return;
        }
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < 33 || !bytes.StartsWith(png) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) is 0 or > 8192 ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) is 0 or > 8192)
            throw new InvalidDataException("A model PNG texture has invalid dimensions or signature.");
    }

    private static bool ValidJpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            return false;
        int offset = 2;
        while (offset + 4 <= bytes.Length && bytes[offset++] == 0xff)
        {
            while (offset < bytes.Length && bytes[offset] == 0xff)
                offset++;
            if (offset + 3 > bytes.Length)
                break;
            int marker = bytes[offset++];
            if (marker is 0xda or 0xd9)
                break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7)
                continue;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset)
                return false;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8)
                    return false;
                int height = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]);
                return width is >= 1 and <= 8192 && height is >= 1 and <= 8192;
            }
            offset += length;
        }
        return false;
    }
}
