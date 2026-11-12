// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace SpaceTangent.Application;

public sealed record LegacyFrameSummary(int RecordingSchemaMinor, int FrameSchema, int Frames, int Strings, int Geometries,
    int StarFields, long? FirstSequence, long? LastSequence, double? FirstTdb, double? LastTdb);

/// <summary>Reads and validates the archive's record envelopes and observer epoch metadata.</summary>
public static class LegacyFrameInspection
{
    private static readonly uint[] Crc = MakeTable();

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static LegacyFrameSummary Inspect(Stream stream, CancellationToken cancellation = default) =>
        Inspect(stream, null, cancellation);

    internal delegate void FrameVisitor(ReadOnlySpan<byte> prefix, int minor, IReadOnlyList<string> strings);

    internal static LegacyFrameSummary Inspect(Stream stream, FrameVisitor? visitor, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> file = stackalloc byte[24];
        stream.ReadExactly(file);
        int major = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
        int minor = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);
        int schema = BinaryPrimitives.ReadUInt16LittleEndian(file[12..]);
        if (!file[..8].SequenceEqual("CELFRAME"u8) || major != 1 || minor > 11 || schema != 2 ||
            BinaryPrimitives.ReadUInt16LittleEndian(file[14..]) != 0xfeff ||
            BinaryPrimitives.ReadInt32LittleEndian(file[16..]) != 24 || BinaryPrimitives.ReadInt32LittleEndian(file[20..]) != 0)
            throw new InvalidDataException("Unsupported archived frame recording header.");

        Span<byte> record = stackalloc byte[5];
        Span<byte> digest = stackalloc byte[4];
        byte[] buffer = new byte[64 * 1024];
        List<string>? names = visitor is null ? null : [];
        int frames = 0, strings = 0, geometries = 0, starFields = 0;
        long? firstSequence = null, lastSequence = null;
        double? firstTdb = null, lastTdb = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            stream.ReadExactly(record);
            int kind = record[0];
            int length = BinaryPrimitives.ReadInt32LittleEndian(record[1..]);
            int maximum = kind switch
            {
                1 => 512,
                2 => 4 + 2048 * 24,
                3 => 1024 * 1024,
                4 => 12,
                5 when minor >= 10 => 256 * 1024 * 1024,
                _ => throw new InvalidDataException("Unknown archived frame record.")
            };
            if (length < 0 || length > maximum)
                throw new InvalidDataException("Archived frame record exceeds its length limit.");
            int read = Math.Min(length, buffer.Length);
            stream.ReadExactly(buffer.AsSpan(0, read));
            uint crc = Update(0xffffffffu, record);
            crc = Update(crc, buffer.AsSpan(0, read));
            if (kind == 3)
            {
                if (length < 41 || frames >= 1_000_000)
                    throw new InvalidDataException("Archived frame count or header exceeds its limits.");
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                firstSequence ??= sequence;
                lastSequence = sequence;
                if (buffer[40] == 0)
                {
                    long day = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
                    long nanos = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32));
                    if (nanos is < 0 or >= 86_400_000_000_000L)
                        throw new InvalidDataException("Archived frame epoch is invalid.");
                    lastTdb = day + nanos / 86_400_000_000_000d;
                    firstTdb ??= lastTdb;
                }
                visitor?.Invoke(buffer.AsSpan(0, read), minor, names!);
                frames++;
            }
            else if (kind == 4 && (length != 12 || BinaryPrimitives.ReadInt32LittleEndian(buffer) != frames ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4)) != strings ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)) != geometries))
                throw new InvalidDataException("Archived frame footer counts differ.");
            else if (kind == 1)
            {
                if (names is not null)
                {
                    if (names.Count >= 65_536)
                        throw new InvalidDataException("Archived camera replay exceeds the name table limit.");
                    names.Add(Utf8.GetString(buffer.AsSpan(0, read)));
                }
                strings++;
            }
            else if (kind == 2)
                geometries++;
            else if (kind == 5)
                starFields++;

            for (int remaining = length - read; remaining > 0; remaining -= read)
            {
                cancellation.ThrowIfCancellationRequested();
                read = Math.Min(remaining, buffer.Length);
                stream.ReadExactly(buffer.AsSpan(0, read));
                crc = Update(crc, buffer.AsSpan(0, read));
            }
            stream.ReadExactly(digest);
            if (BinaryPrimitives.ReadUInt32LittleEndian(digest) != ~crc)
                throw new InvalidDataException("Archived frame record checksum differs.");
            if (kind == 4)
            {
                if (stream.ReadByte() != -1)
                    throw new InvalidDataException("Archived frame recording has trailing data.");
                return new(minor, schema, frames, strings, geometries, starFields, firstSequence, lastSequence, firstTdb, lastTdb);
            }
        }
    }

    private static uint Update(uint value, ReadOnlySpan<byte> bytes)
    {
        foreach (byte item in bytes)
            value = Crc[(value ^ item) & 0xff] ^ (value >> 8);
        return value;
    }

    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (int i = 0; i < table.Length; i++)
        {
            uint value = (uint)i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
