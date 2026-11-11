// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class BrightSkyOverlay : Control
{
    internal readonly record struct SkyFrame(JulianInstant Epoch, SkyLayerSettings Layers, ObjectId? Selection,
        Vector3 Forward, Vector3 Right, Vector3 Up, float TangentHalfField, float StarVisibility = 1,
        Vector3? Horizon = null, Vector3? Moon = null, float MoonRadius = 0);

    private static readonly (Vector3 From, Vector3 To)[] Grid = MakeGrid().ToArray();
    private BrightSkyData _data = null!;
    private SkyFrame _frame;
    private bool _fillBackground;

    internal void Configure(BrightSkyData data, bool fillBackground)
    {
        _data = data;
        _fillBackground = fillBackground;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        Resized += QueueRedraw;
        Hide();
    }

    internal void ShowFrame(SkyFrame frame)
    {
        Visible = frame.Layers.Visible;
        if (_frame == frame)
            return;
        _frame = frame;
        if (Visible)
            QueueRedraw();
    }

    internal ObjectId? Pick(Vector2 point)
    {
        if (!IsVisibleInTree() || !_frame.Layers.Stars || _frame.StarVisibility < .12f)
            return null;
        float best = 10 * 10;
        ObjectId? selected = null;
        foreach (var star in _data.Catalogue.Stars)
        {
            if (!TryPoint(star.Id, out Vector2 candidate))
                continue;
            float distance = point.DistanceSquaredTo(candidate);
            if (distance < best)
            {
                best = distance;
                selected = star.Id;
            }
        }
        return selected;
    }

    internal bool TryPoint(ObjectId id, out Vector2 point)
    {
        point = default;
        if (_data.Catalogue.Find(id) is not { } star)
            return false;
        Vector3 direction = Convert(star.DirectionAt(_frame.Epoch));
        if (_frame.Moon is { } moon && direction.Dot(moon) >= MathF.Cos(_frame.MoonRadius))
            return false;
        return Project(direction, out point) && new Rect2(Vector2.Zero, Size).Grow(2).HasPoint(point);
    }

    public override void _Draw()
    {
        if (_fillBackground)
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(.02745f, .03529f, .04706f));
        if (_frame.Layers.EquatorialGrid)
            DrawSegments(Grid, new(.32f, .52f, .70f, .53f));
        if (_frame.Layers.Constellations)
            DrawSegments(_data.Figures, new(.52f, .63f, .85f, .73f));
        if (_frame.StarVisibility <= 0 || (!_frame.Layers.Stars && !_frame.Layers.Labels))
            return;

        var occupied = new List<Rect2>();
        if (_frame.Layers.Labels && _frame.Selection is { } selectedId &&
            _data.Catalogue.Find(selectedId) is { } selectedStar && TryPoint(selectedId, out Vector2 selectedPoint))
            LabelStar(selectedStar, selectedPoint);

        foreach (var star in _data.Catalogue.Stars)
        {
            if (!TryPoint(star.Id, out Vector2 point))
                continue;
            bool selected = star.Id == _frame.Selection;
            if (_frame.Layers.Stars)
            {
                float size = Math.Clamp(3.2f - (float)star.VisualMagnitude * .44f, .7f, 4);
                float alpha = _frame.StarVisibility * Math.Clamp(1 - (float)star.VisualMagnitude * .11f, .30f, 1);
                DrawCircle(point, size, new(.84f, .91f, 1, alpha), antialiased: true);
                if (selected)
                    DrawArc(point, 8, 0, Mathf.Tau, 20, new Color(.55f, .82f, 1), 1, true);
            }
            if (_frame.Layers.Labels && !selected && star.Name.Length > 0 && star.VisualMagnitude <= 2.5)
                LabelStar(star, point);
        }

        void LabelStar(SpaceTangent.Core.Observing.BrightStar star, Vector2 point)
        {
            string text = star.Name.Length > 0 ? star.Name : FormattableString.Invariant($"HIP {star.Hipparcos}");
            Vector2 baseline = point + new Vector2(6, -5);
            Font font = ThemeDB.FallbackFont;
            var bounds = new Rect2(baseline.X, baseline.Y - 13, font.GetStringSize(text, fontSize: 13).X, 16);
            if (occupied.Any(existing => existing.Grow(2).Intersects(bounds)))
                return;
            occupied.Add(bounds);
            DrawString(font, baseline, text, fontSize: 13, modulate: new Color(.77f, .87f, 1, _frame.StarVisibility));
        }
    }

    private void DrawSegments(IReadOnlyList<(Vector3 From, Vector3 To)> lines, Color color)
    {
        var rect = new Rect2(Vector2.Zero, Size).Grow(20);
        foreach (var (from, to) in lines)
        {
            Vector3 start = from, end = to;
            if (_frame.Horizon is { } horizon)
            {
                float first = from.Dot(horizon), last = to.Dot(horizon);
                if (first < 0 && last < 0)
                    continue;
                if (first < 0 || last < 0)
                {
                    Vector3 crossing = from.Lerp(to, first / (first - last)).Normalized();
                    crossing = (crossing + horizon * 1e-6f).Normalized();
                    if (first < 0)
                        start = crossing;
                    else
                        end = crossing;
                }
            }
            if (Project(start, out Vector2 a) && Project(end, out Vector2 b) &&
                new Rect2(a, Vector2.Zero).Expand(b).Grow(1).Intersects(rect))
                DrawLine(a, b, color, 1, true);
        }
    }

    private bool Project(Vector3 direction, out Vector2 point)
    {
        point = default;
        if (_frame.Horizon is { } horizon && direction.Dot(horizon) < 0)
            return false;
        float depth = direction.Dot(_frame.Forward);
        if (depth < .02f || _frame.TangentHalfField <= 0 || Size.Y <= 0)
            return false;
        float scale = Size.Y / (2 * depth * _frame.TangentHalfField);
        point = Size / 2 + new Vector2(direction.Dot(_frame.Right), -direction.Dot(_frame.Up)) * scale;
        return true;
    }

    private static IEnumerable<(Vector3, Vector3)> MakeGrid()
    {
        for (int ra = 0; ra < 360; ra += 30)
        for (int dec = -90; dec < 90; dec += 5)
            yield return (BrightSkyData.Equatorial(ra, dec), BrightSkyData.Equatorial(ra, dec + 5));
        for (int dec = -75; dec <= 75; dec += 15)
        for (int ra = 0; ra < 360; ra += 5)
            yield return (BrightSkyData.Equatorial(ra, dec), BrightSkyData.Equatorial(ra + 5, dec));
    }

    internal static Vector3 Convert(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);
}
