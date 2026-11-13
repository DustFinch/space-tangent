// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed record RecordedOrbit(string Name, IReadOnlyList<Vector3d> Points, Vector3d Anchor,
    double Scale, Basis3d ToCamera, Vector3d? Color, double Fade)
{
    public Vector3d CameraPoint(int index) => ToCamera.Transform(Anchor + Points[index]) * Scale;
}

public readonly record struct RecordedLabel(string Text, Vector3d CameraPosition, int Kind,
    double Opacity, Vector3d? Color, double MarkerRadians);

public readonly record struct RecordedBodyPoint(string Name, double Magnitude, Vector3d Color, double Fade, bool ReplacesSurface);

public readonly record struct RecordedCometTail(string Name, Vector3d Head, Vector3d Direction, Vector3d Trail,
    double Length, double Width, double Brightness, Vector3d Color, double ComaRadius);

internal sealed class LegacyFrameOverlays
{
    private readonly List<IReadOnlyList<Vector3d>> _geometry = [];
    private RecordedOrbit[] _lastOrbits = [];
    private RecordedLabel[] _lastLabels = [];
    private RecordedBodyPoint[] _lastPoints = [];
    private RecordedCometTail[] _lastTails = [];
    private IReadOnlyList<RecordedCometTail> _tails = [];
    private IReadOnlyDictionary<string, RecordedBodyPoint> _points =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, RecordedBodyPoint>(new Dictionary<string, RecordedBodyPoint>());
    private IReadOnlyList<RecordedOrbit> _orbits = [];
    private IReadOnlyList<RecordedLabel> _labels = [];
    private int _geometryBytes, _retained;

    internal void AddOrbitGeometry(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        uint count = reader.U32();
        if (count is < 2 or > 2048 || data.Length != 4 + count * 24 || _geometry.Count == 65_536 ||
            (_geometryBytes += data.Length - 4) > 128 * 1024 * 1024)
            throw new InvalidDataException("Archived orbit geometry exceeds its limits.");
        var points = new Vector3d[count];
        for (int index = 0; index < points.Length; index++)
            points[index] = reader.Vector();
        for (int index = 0; index < points.Length; index++)
            points[index] += reader.Vector();
        _geometry.Add(Array.AsReadOnly(points));
    }

    internal (IReadOnlyList<RecordedOrbit>, IReadOnlyList<RecordedLabel>, IReadOnlyDictionary<string, RecordedBodyPoint>, IReadOnlyList<RecordedCometTail>, int StellarOffset) Read(ReadOnlySpan<byte> data,
        int offset, int minor, IReadOnlyList<string> strings)
    {
        int stars = Count(data, 100, 256), orbitCount = Count(data, 102, 128);
        int shadows = Count(data, 104, 768), points = Count(data, 106, 192), labelCount = Count(data, 108, 256);
        var reader = new Reader(data[offset..]);
        reader.Skip(stars * 20);
        var rotation = new Reader(data[65..81]);
        Vector3d imaginary = rotation.Vector();
        double real = rotation.Float();
        var worldToCamera = new Basis3d(Turn(new(1, 0, 0)), Turn(new(0, 1, 0)), Turn(new(0, 0, 1)));
        var orbits = new RecordedOrbit[orbitCount];
        for (int index = 0; index < orbits.Length; index++)
        {
            string name = reader.Text(strings);
            uint geometry = reader.U32();
            Vector3d anchor = reader.Vector() + reader.Vector();
            double scale = reader.Float();
            int worldFrame = reader.Byte();
            if (geometry >= _geometry.Count || scale <= 0 || worldFrame > 1)
                throw new InvalidDataException("Archived orbit geometry or placement is invalid.");
            Vector3d? color = minor >= 9 ? reader.Color() : null;
            double fade = minor >= 9 ? reader.Unit() : 0;
            orbits[index] = new(name, _geometry[(int)geometry], anchor, scale,
                worldFrame == 1 ? worldToCamera : Basis3d.Identity, color, fade);
        }

        reader.Skip(shadows * 20);
        var bodyPoints = new RecordedBodyPoint[points];
        for (int index = 0; index < bodyPoints.Length; index++)
        {
            string name = reader.Text(strings);
            double magnitude = reader.Float();
            Vector3d color = reader.Vector();
            double fade = reader.Unit();
            byte replaces = reader.Byte();
            if (replaces > 1)
                throw new InvalidDataException("Invalid archived point visibility flag.");
            bodyPoints[index] = new(name, magnitude, color, fade, replaces == 1);
        }
        var labels = new RecordedLabel[labelCount];
        for (int index = 0; index < labels.Length; index++)
        {
            _ = reader.Text(strings);
            string text = reader.Text(strings);
            Vector3d position = reader.Vector();
            int kind = reader.Byte();
            double opacity = reader.Unit();
            Vector3d? color = minor >= 9 ? reader.Color() : null;
            double marker = minor >= 9 ? reader.Float() : 0;
            if (kind > 9 || marker < 0 || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("Archived label geometry is invalid.");
            labels[index] = new(text, position, kind, opacity, color, marker);
        }

        if (!orbits.AsSpan().SequenceEqual(_lastOrbits))
        {
            Charge(orbits.Length);
            _orbits = Array.AsReadOnly(_lastOrbits = orbits);
        }
        if (!labels.AsSpan().SequenceEqual(_lastLabels))
        {
            Charge(labels.Length);
            _labels = Array.AsReadOnly(_lastLabels = labels);
        }
        if (!bodyPoints.AsSpan().SequenceEqual(_lastPoints))
        {
            Charge(bodyPoints.Length);
            var byName = new Dictionary<string, RecordedBodyPoint>(StringComparer.Ordinal);
            foreach (var point in bodyPoints)
                if (!byName.TryAdd(point.Name, point))
                    throw new InvalidDataException("Duplicate archived body point.");
            _lastPoints = bodyPoints;
            _points = new System.Collections.ObjectModel.ReadOnlyDictionary<string, RecordedBodyPoint>(byName);
        }
        var tails = new RecordedCometTail[minor >= 9 ? Count(data, 110, 32) : 0];
        for (int index = 0; index < tails.Length; index++)
        {
            var tail = new RecordedCometTail(reader.Text(strings), reader.Vector(), reader.Vector(), reader.Vector(),
                reader.Float(), reader.Float(), reader.Unit(), reader.Vector(), reader.Float());
            if (tail.Length <= 0 || tail.Width <= 0 || tail.ComaRadius < 0 ||
                Math.Abs(Vector3d.Dot(tail.Direction, tail.Direction) - 1) > 2e-6)
                throw new InvalidDataException("Invalid archived comet tail geometry.");
            tails[index] = tail;
        }
        if (!tails.AsSpan().SequenceEqual(_lastTails))
        {
            Charge(tails.Length);
            _tails = Array.AsReadOnly(_lastTails = tails);
        }
        return (_orbits, _labels, _points, _tails, data.Length - reader.Remaining);

        Vector3d Turn(Vector3d value)
        {
            Vector3d twice = Vector3d.Cross(imaginary, value) * 2;
            return value + twice * real + Vector3d.Cross(imaginary, twice);
        }
    }

    private void Charge(int entries)
    {
        if ((_retained += entries) > 1_000_000)
            throw new InvalidDataException("Archived replay exceeds the orbit and label budget.");
    }

    private static int Count(ReadOnlySpan<byte> data, int offset, int maximum)
    {
        int value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        return value <= maximum ? value : throw new InvalidDataException("Archived overlay count exceeds its limit.");
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_remaining.Length < count)
                throw new InvalidDataException("Archived overlay is truncated.");
            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }

        internal int Remaining => _remaining.Length;
        internal void Skip(int count) => _ = Take(count);
        internal byte Byte() => Take(1)[0];
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal double Float()
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            return float.IsFinite(value) ? value : throw new InvalidDataException("Archived overlay value is not finite.");
        }

        internal Vector3d Vector() => new(Float(), Float(), Float());
        internal double Unit()
        {
            double value = Float();
            return value is >= 0 and <= 1 ? value : throw new InvalidDataException("Archived overlay opacity or fade is out of range.");
        }

        internal Vector3d? Color() => Byte() switch
        {
            0 => null,
            1 => Vector(),
            _ => throw new InvalidDataException("Archived overlay color flag is invalid.")
        };

        internal string Text(IReadOnlyList<string> strings)
        {
            uint index = U32();
            return index < strings.Count ? strings[(int)index] :
                throw new InvalidDataException("Archived overlay refers to an undefined string.");
        }
    }
}
