// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedMissionObservation(MissionObservation Style, IReadOnlyList<MissionFootprint> Footprints,
    double RadiusKilometers, bool SphericalDisplay);

internal sealed class MissionObservationRendering
{
    private readonly PreparedMissionObservation _source;
    private readonly MeshInstance3D _node;
    private readonly ImmediateMesh _mesh = new();
    private readonly StandardMaterial3D _material = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };
    private readonly double _near, _far;
    private int _published = -1;
    private bool _triangles, _lines;

    public MissionObservationRendering(MeshInstance3D node, PreparedMissionObservation source)
    {
        _node = node;
        _source = source;
        _node.Mesh = _mesh;
        _node.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _near = source.Footprints.Count == 0 ? 0 : source.Footprints.Min(footprint => footprint.MeanRangeKilometers);
        _far = source.Footprints.Count == 0 ? 0 : source.Footprints.Max(footprint => footprint.MeanRangeKilometers);
    }

    internal int Count => _node.Visible ? _published : 0;

    public void Draw(JulianInstant epoch, Transform3D transform)
    {
        var prints = _source.Footprints;
        int low = 0, high = prints.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (prints[middle].Epoch.CompareTo(epoch) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        if (low != _published)
            Publish(low);
        _node.Transform = transform;
        _node.Visible = low > 0;
    }

    private void Publish(int count)
    {
        _published = count;
        _mesh.ClearSurfaces();
        _triangles = false;
        _lines = false;
        var outlines = new List<(IReadOnlyList<Vector3d> Points, Color Color)>();
        foreach (var group in _source.Footprints.Take(count).GroupBy(footprint => footprint.Group))
        {
            MissionFootprint[] prints = group.ToArray();
            if (!prints[0].Swath || prints.Length == 1)
            {
                foreach (var print in prints)
                    AddFootprint(print, (float)_source.Style.Opacity);
                continue;
            }

            var centers = prints.Select(print => print.Points.Aggregate(default(Vector3d), (sum, point) => sum + point) /
                print.Points.Count).ToArray();
            var left = new Vector3d[prints.Length];
            var right = new Vector3d[prints.Length];
            for (int index = 0; index < prints.Length; index++)
            {
                Vector3d direction = centers[Math.Min(index + 1, prints.Length - 1)] - centers[Math.Max(index - 1, 0)];
                Vector3d across = Vector3d.Cross(direction, centers[index]);
                Vector3d center = centers[index];
                left[index] = prints[index].Points.MaxBy(point => Vector3d.Dot(point - center, across));
                right[index] = prints[index].Points.MinBy(point => Vector3d.Dot(point - center, across));
            }
            for (int index = 0; index < prints.Length - 1; index++)
                AddPolygon([left[index], left[index + 1], right[index + 1], right[index]],
                    ColorFor(prints[index], (float)_source.Style.Opacity));
            AddFootprint(prints[0], (float)_source.Style.Opacity * .5f);
            AddFootprint(prints[^1], (float)_source.Style.Opacity * .5f);
        }
        if (_triangles)
            _mesh.SurfaceEnd();
        foreach (var outline in outlines)
        {
            if (!_lines)
            {
                _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
                _lines = true;
            }
            for (int index = 0; index < outline.Points.Count; index++)
            {
                AddVertex(outline.Points[index], outline.Color);
                AddVertex(outline.Points[(index + 1) % outline.Points.Count], outline.Color);
            }
        }
        if (_lines)
            _mesh.SurfaceEnd();

        void AddFootprint(MissionFootprint print, float opacity)
        {
            AddPolygon(print.Points, ColorFor(print, opacity));
            outlines.Add((print.Points, ColorFor(print, Math.Clamp((float)_source.Style.Opacity * 2, .4f, 1))));
        }
    }

    private Color ColorFor(MissionFootprint footprint, float opacity)
    {
        var rgb = _source.Style.Color;
        if (!_source.Style.ByResolution)
            return new((float)rgb.X, (float)rgb.Y, (float)rgb.Z, opacity);
        float fraction = _far > _near ? (float)((footprint.MeanRangeKilometers - _near) / (_far - _near)) : 0;
        Color color = fraction < .5f ? new Color(1, 0, 0).Lerp(new(1, 1, 0), fraction * 2) :
            new Color(1, 1, 0).Lerp(new(0, .4f, 1), (fraction - .5f) * 2);
        return color with { A = opacity };
    }

    private void AddPolygon(IReadOnlyList<Vector3d> points, Color color)
    {
        if (color.A <= 0)
            return;
        if (!_triangles)
        {
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, _material);
            _triangles = true;
        }
        for (int index = 1; index < points.Count - 1; index++)
        {
            AddVertex(points[0], color);
            AddVertex(points[index], color);
            AddVertex(points[index + 1], color);
        }
    }

    private void AddVertex(Vector3d point, Color color)
    {
        // Raise display vertices above the rendered globe. The physical footprints keep their exact ellipsoid heights.
        double radius = _source.SphericalDisplay ? point.Length : _source.RadiusKilometers;
        Vector3d display = point * (1.0008 / radius);
        _mesh.SurfaceSetColor(color);
        _mesh.SurfaceAddVertex(new((float)display.X, (float)display.Z, (float)-display.Y));
    }
}
