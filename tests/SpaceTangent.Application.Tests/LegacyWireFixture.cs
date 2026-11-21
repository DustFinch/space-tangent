// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace SpaceTangent.Application.Tests;

// Hand-authored wire examples. No production recorder, decoder, or CRC helper builds these inputs.
internal sealed class LegacyWireFixture(int minor = 11)
{
    internal int Minor { get; } = minor;
    private readonly List<(byte Kind, byte[] Data)> _records = [];
    internal int BodyStart => Minor >= 9 ? 112 : 110;
    internal int BodyLength => 69 + (Minor >= 4 ? 24 : 0) + (Minor >= 5 ? 36 : 0) + (Minor >= 6 ? 8 : 0);

    internal LegacyWireFixture Names(params string[] names)
    {
        foreach (string name in names) Record(1, Encoding.UTF8.GetBytes(name));
        return this;
    }

    internal LegacyWireFixture Record(byte kind, byte[] payload)
    {
        _records.Add((kind, payload));
        return this;
    }

    internal byte[] Body()
    {
        var body = new byte[BodyLength + (Minor >= 8 ? 1 : 0)];
        F(body, 12, -100); // camera Z; position is (0, 0, -100) kilometres at scale 1
        F(body, 16, 1);
        F(body, 32, 1); // identity quaternion
        F(body, 40, 1); // first light along Y
        U(body, 48, 1); // material name
        F(body, 60, 2); // atmosphere encloses the solid radius
        F(body, 65, 1); // polar scale
        if (Minor >= 5)
        {
            F(body, 93, 1); F(body, 97, .5f); F(body, 101, .25f);
            F(body, 113, 1); // second light along Z
            F(body, 117, .125f); F(body, 121, .25f); F(body, 125, .5f);
        }
        if (Minor >= 6) { F(body, 129, .004f); F(body, 133, .002f); }
        return body;
    }

    internal byte[] Frame(byte[]? body = null, byte[]? overlays = null, long sequence = 1)
    {
        body ??= Body();
        overlays ??= new byte[(Minor >= 1 ? 1 : 0) + (Minor >= 10 ? 1 : 0)];
        var data = new byte[BodyStart + body.Length + overlays.Length];
        BinaryPrimitives.WriteInt64LittleEndian(data, sequence);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), 2451545);
        F(data, 45, 45);
        D(data, 57, 1);
        F(data, 77, 1);
        F(data, 89, -1000); F(data, 93, 1);
        H(data, 98, 1);
        body.CopyTo(data, BodyStart);
        overlays.CopyTo(data, BodyStart + body.Length);
        return data;
    }

    internal byte[] File(Action<byte[]>? changeHeader = null, Action<byte[]>? changeFooter = null)
    {
        var header = new byte[24];
        "CELFRAME"u8.CopyTo(header);
        H(header, 8, 1); H(header, 10, (ushort)Minor); H(header, 12, 2); H(header, 14, 0xfeff);
        U(header, 16, 24);
        changeHeader?.Invoke(header);
        using var stream = new MemoryStream();
        stream.Write(header);
        foreach (var (kind, data) in _records) Envelope(stream, kind, data);
        var footer = new byte[12];
        U(footer, 0, (uint)_records.Count(item => item.Kind == 3));
        U(footer, 4, (uint)_records.Count(item => item.Kind == 1));
        U(footer, 8, (uint)_records.Count(item => item.Kind == 2));
        changeFooter?.Invoke(footer);
        Envelope(stream, 4, footer);
        return stream.ToArray();
    }

    private static void Envelope(Stream stream, byte kind, byte[] data)
    {
        byte[] bytes = new byte[5 + data.Length];
        bytes[0] = kind; U(bytes, 1, (uint)data.Length); data.CopyTo(bytes, 5);
        // CRC-32/ISO-HDLC, bitwise rather than the decoder's lookup-table implementation.
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        stream.Write(bytes);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(checksum, ~crc);
        stream.Write(checksum);
    }

    internal static void H(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void U(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    internal static void F(byte[] bytes, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
    internal static void D(byte[] bytes, int offset, double value) => BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset), value);
}
