// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed record RecordedStarField(int Count, byte Flags, ReadOnlyMemory<byte> Data)
{
    public ReadOnlySpan<float> Directions => MemoryMarshal.Cast<byte, float>(Data.Span[..(Count * 12)]);
    public ReadOnlySpan<float> Magnitudes => MemoryMarshal.Cast<byte, float>(Data.Span[(Count * 12)..(Count * 16)]);
    public ReadOnlySpan<byte> Colors => Data.Span[(Count * 16)..(Count * 19)];
    public ReadOnlySpan<float> Distances => (Flags & 1) == 0 ? [] :
        MemoryMarshal.Cast<byte, float>(Data.Span[(Count * 19)..(Count * 23)]);
    public ReadOnlySpan<float> Velocities => (Flags & 2) == 0 ? [] :
        MemoryMarshal.Cast<byte, float>(Data.Span[(Count * (19 + ((Flags & 1) != 0 ? 4 : 0)))..])[..(Count * 3)];
}

public sealed record RecordedStellarView(RecordedStarField Background, RecordedStarField? Periodic,
    ReadOnlyMemory<byte> PeriodicPositions, Vector3d Observer, Vector3d NearOrigin, float NearCut,
    float FaintestMagnitude, float BrightnessScale, float SaturationPoint)
{
    public RecordedGroundStars? Ground { get; init; }
    public Basis3d ToCamera { get; init; }
}

public readonly record struct RecordedGroundStars(Vector3d Zenith, float Refraction, float Twinkle, float Glow,
    Vector3d Beta, float Time);

internal sealed class LegacyFrameStars
{
    private RecordedStarField? _background, _periodic;
    private ReadOnlyMemory<byte> _positions;
    private long _retained;

    internal void ReadField(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || bytes[0] > 1)
            throw new InvalidDataException("Invalid archived star field header.");
        int slot = bytes[0], count = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (count < 0 || count > (slot == 0 ? 3_000_000 : 8192))
            throw new InvalidDataException("Invalid archived star count.");
        RecordedStarField? field = null;
        if (count != 0)
        {
            if (bytes.Length < 6)
                throw new InvalidDataException("Truncated archived star field.");
            byte flags = bytes[5];
            int stride = 19 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 2) != 0 ? 12 : 0) + ((flags & 4) != 0 ? 4 : 0);
            if (flags > 7 || (slot == 1 && (flags & 1) == 0) || bytes.Length != 6 + count * stride)
                throw new InvalidDataException("Invalid archived star field arrays.");
            Charge(bytes.Length - 6);
            field = new(count, flags, bytes[6..].ToArray());
            foreach (float value in field.Directions)
                if (!float.IsFinite(value) || Math.Abs(value) > 1.001f)
                    throw new InvalidDataException("Invalid archived stellar direction.");
            foreach (float value in field.Magnitudes)
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Invalid archived stellar magnitude.");
            foreach (float value in field.Distances)
                if (!float.IsFinite(value) || value <= 0)
                    throw new InvalidDataException("Invalid archived stellar distance.");
            foreach (float value in field.Velocities)
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Invalid archived stellar velocity.");
        }
        else if (bytes.Length is not (5 or 6) || (bytes.Length == 6 && bytes[5] != 0))
            throw new InvalidDataException("Invalid empty archived star field.");
        if (slot == 0)
            _background = field;
        else
            _periodic = field;
    }

    internal RecordedStellarView? ReadView(ReadOnlySpan<byte> frame, int offset)
    {
        if (offset >= frame.Length || frame[offset] > 1)
            throw new InvalidDataException("Invalid archived stellar view flag.");
        if (frame[offset++] == 0)
            return null;
        if (_background is null || frame.Length - offset < 41)
            throw new InvalidDataException("Missing archived stellar view or background.");
        var data = frame[offset..];
        var observer = Vector(data);
        var nearOrigin = Vector(data[12..]);
        float nearCut = Float(data[24..]), faintest = Float(data[28..]), brightness = Float(data[32..]), saturation = Float(data[36..]);
        if (nearCut < 0 || brightness < 0 || data[40] > 1)
            throw new InvalidDataException("Invalid archived stellar view.");
        RecordedGroundStars? ground = null;
        int start = 41;
        if (data[40] != 0)
        {
            if (data.Length < start + 40)
                throw new InvalidDataException("Truncated archived ground stellar view.");
            var air = data[start..];
            ground = new(Vector(air), Float(air[12..]), Float(air[16..]), Float(air[20..]), Vector(air[24..]), Float(air[36..]));
            start += 40;
        }
        var positions = data[start..];
        if (positions.Length != (_periodic?.Count ?? 0) * 12)
            throw new InvalidDataException("Invalid archived moving star positions.");
        if (!positions.SequenceEqual(_positions.Span))
        {
            foreach (float value in MemoryMarshal.Cast<byte, float>(positions))
                if (!float.IsFinite(value))
                    throw new InvalidDataException("Invalid archived moving star position.");
            Charge(positions.Length);
            _positions = positions.ToArray();
        }
        var imaginary = Vector(frame[65..]);
        float real = Float(frame[77..]);
        return new(_background, _periodic, _positions, observer, nearOrigin, nearCut, faintest, brightness, saturation)
        {
            Ground = ground,
            ToCamera = new(Turn(new(1, 0, 0)), Turn(new(0, 1, 0)), Turn(new(0, 0, 1)))
        };

        Vector3d Turn(Vector3d axis)
        {
            var twice = Vector3d.Cross(imaginary, axis) * 2;
            return axis + twice * real + Vector3d.Cross(imaginary, twice);
        }
    }

    private void Charge(int bytes)
    {
        if ((_retained += bytes) > 160 * 1024 * 1024)
            throw new InvalidDataException("Archived stellar resources exceed the replay budget.");
    }

    private static Vector3d Vector(ReadOnlySpan<byte> bytes) => new(Float(bytes), Float(bytes[4..]), Float(bytes[8..]));
    private static float Float(ReadOnlySpan<byte> bytes)
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return float.IsFinite(value) ? value : throw new InvalidDataException("Nonfinite archived stellar value.");
    }
}
