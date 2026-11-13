// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal sealed class SkyBoundaryMap
{
    private sealed record Loop(string Abbreviation, Vector3d Center, double MinimumDot, Vector3d[] Vertices);
    private readonly List<Loop> _loops = [];
    internal List<(Vector3 From, Vector3 To)> Segments { get; } = [];

    internal static SkyBoundaryMap Read(TextReader reader)
    {
        var map = new SkyBoundaryMap();
        var vertices = new List<Vector3d>();
        string? group = null;
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 ||
                !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double hours) ||
                !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double degrees) ||
                !(hours is >= 0 and <= 24) || !(degrees is >= -90 and <= 90))
                throw new InvalidDataException("Invalid constellation boundary vertex.");
            if (fields[2] == "XXX")
                break;
            if (group != fields[2])
            {
                map.AddLoop(group, vertices);
                vertices.Clear();
                group = fields[2];
            }
            var (sinRa, cosRa) = Math.SinCos(hours * Math.PI / 12);
            var (sinDec, cosDec) = Math.SinCos(degrees * Math.PI / 180);
            var direction = new Vector3d(cosDec * cosRa, cosDec * sinRa, sinDec);
            if (vertices.Count > 0)
                map.Segments.Add((ToVector(vertices[^1]), ToVector(direction)));
            vertices.Add(direction);
        }
        map.AddLoop(group, vertices);
        return map;
    }

    internal string? At(Vector3d direction)
    {
        direction /= direction.Length;
        foreach (var loop in _loops)
        {
            // A spherical loop also winds around the antipode. Keep the loop on the queried side.
            if (Vector3d.Dot(direction, loop.Center) < loop.MinimumDot)
                continue;
            double turn = 0;
            var a = loop.Vertices[^1];
            foreach (var b in loop.Vertices)
            {
                turn += Math.Atan2(Vector3d.Dot(direction, Vector3d.Cross(a, b)),
                    Vector3d.Dot(a, b) - Vector3d.Dot(direction, a) * Vector3d.Dot(direction, b));
                a = b;
            }
            if (Math.Abs(turn) > Math.PI)
                return loop.Abbreviation;
        }
        return null;
    }

    private void AddLoop(string? group, List<Vector3d> vertices)
    {
        if (group is null || vertices.Count < 3)
            return;
        Vector3d center = default;
        foreach (var vertex in vertices)
            center += vertex;
        if (center.Length == 0)
            return;
        center /= center.Length;
        // For a cap smaller than a hemisphere, its shortest boundary arcs stay inside the cap.
        double minimumDot = Math.Max(0, vertices.Min(vertex => Vector3d.Dot(center, vertex)) - 1e-9);
        _loops.Add(new(group, center, minimumDot, vertices.ToArray()));
    }

    private static Vector3 ToVector(Vector3d direction) => new((float)direction.X, (float)direction.Y, (float)direction.Z);
}
