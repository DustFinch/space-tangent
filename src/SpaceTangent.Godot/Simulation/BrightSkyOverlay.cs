// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal partial class BrightSkyOverlay : Control
{
    internal readonly record struct SkyFrame(JulianInstant Epoch, SkyLayerSettings Layers, ObjectId? Selection,
        Vector3 Forward, Vector3 Right, Vector3 Up, float TangentHalfField, float StarVisibility = 1,
        Vector3? Horizon = null, Vector3? Moon = null, float MoonRadius = 0, bool ShowCatalogue = true,
        GroundSkyConditions? Conditions = null, EarthSite Site = default, double? OpticalLimit = null,
        Vector3d ObserverFromSun = default, Vector3d ObserverBeta = default, Basis3d? SiteAxes = null,
        (double FirstDay, Vector3?[] Directions)? BodyPath = null);

    private static readonly (Vector3 From, Vector3 To)[] Grid = MakeGrid().ToArray();
    private static readonly (Vector3 From, Vector3 To)[] EclipticLine = Enumerable.Range(0, 360)
        .Select(degree => (EclipticDirection(degree, 0), EclipticDirection(degree + 1, 0))).ToArray();
    private static readonly (Vector3 From, Vector3 To)[] EclipticGrid = MakeAlternateGrid(EclipticDirection).ToArray();
    private static readonly (Vector3 From, Vector3 To)[] GalacticGrid = MakeAlternateGrid(GalacticDirection).ToArray();
    private BrightSkyData _data = null!;
    private SkyFrame _frame;
    private bool _fillBackground;
    private double _scintillationTime;
    private ulong _nextTwinkle;

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || !_frame.Layers.Stars || _frame.StarVisibility <= 0 ||
            _frame.Conditions is not { Scintillation: true })
            return;
        ulong now = Time.GetTicksMsec();
        if (now < _nextTwinkle)
            return;
        _nextTwinkle = now + 40;
        _scintillationTime = now / 1000.0;
        QueueRedraw();
    }

    internal bool HasTransientStars => _data.Transients.Stars.Count != 0;

    internal bool TransientVisible(ObjectId id) => Visible && _data.Transients.Find(id) is { } star &&
        TryTransientPoint(star, out _, out _);

    internal void Configure(BrightSkyData data, bool fillBackground)
    {
        _data = data;
        _fillBackground = fillBackground;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        Resized += QueueRedraw;
        Hide();
    }

    internal void SetFillBackground(bool fill)
    {
        if (_fillBackground == fill)
            return;
        _fillBackground = fill;
        QueueRedraw();
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
        if (!IsVisibleInTree() || !_frame.ShowCatalogue || !_frame.Layers.Stars || _frame.StarVisibility < .12f)
            return null;
        float best = 10 * 10;
        ObjectId? selected = null;
        foreach (var star in _data.Catalogue.Stars)
        {
            if (!TryStarPoint(star, out Vector2 candidate, out _, out _))
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
        return _data.Catalogue.Find(id) is { } star && TryStarPoint(star, out point, out _, out _);
    }

    private bool TryStarPoint(BrightStar star, out Vector2 point, out float magnitude, out float scintillation) =>
        TrySkyObject(star.DirectionAt(_frame.Epoch), star.VisualMagnitude, star.Hipparcos * .61803398875, 2,
            out point, out magnitude, out scintillation);

    private bool TrySkyObject(Vector3d direction, double visualMagnitude, double phase, float margin,
        out Vector2 point, out float magnitude, out float scintillation)
    {
        magnitude = (float)visualMagnitude;
        scintillation = 1;
        point = default;
        direction = ApparentSky.Aberrate(direction, _frame.ObserverBeta);
        if (_frame.Conditions is { } air && _frame.Horizon is { } zenith)
            direction = air.RefractDirection(direction, new(zenith.X, zenith.Y, zenith.Z), _frame.Site);
        Vector3 seen = Convert(direction);
        if ((_frame.Moon is { } moon && seen.Dot(moon) >= MathF.Cos(_frame.MoonRadius)) ||
            !Project(seen, out point) || !new Rect2(Vector2.Zero, Size).Grow(margin).HasPoint(point))
            return false;
        if (_frame.Conditions is { } conditions && _frame.Horizon is { } horizon)
        {
            double altitude = Math.Asin(Math.Clamp(seen.Dot(horizon), -1, 1)) * 180 / Math.PI;
            magnitude += (float)conditions.AttenuationMagnitude(altitude);
            if (magnitude > (_frame.OpticalLimit ?? conditions.LimitingMagnitude()))
                return false;
            scintillation = (float)conditions.ScintillationFactor(altitude, _scintillationTime, phase);
        }
        return true;
    }

    public override void _Draw()
    {
        if (_fillBackground && _frame.ShowCatalogue)
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(.02745f, .03529f, .04706f));
        var occupied = new List<Rect2>();
        bool customConstellations = DrawCulture(occupied);
        DrawSkyMarkings();
        if (_frame.Layers.ConstellationBoundaries)
            DrawSegments(_data.Boundaries, new(.38f, .55f, .73f, .6f));
        if (_frame.Layers.EclipticLine)
            DrawSegments(EclipticLine, new(.75f, .52f, .22f, .72f));
        if (_frame.Layers.EclipticGrid)
            DrawSegments(EclipticGrid, new(.67f, .48f, .27f, .48f));
        if (_frame.Layers.GalacticGrid)
            DrawSegments(GalacticGrid, new(.36f, .65f, .48f, .52f));
        if (!_frame.ShowCatalogue)
        {
            DrawTransients();
            return;
        }
        if (_frame.Layers.EquatorialGrid)
            DrawSegments(Grid, new(.32f, .52f, .70f, .53f));
        if (_frame.Layers.Constellations && !customConstellations)
            DrawSegments(_data.Figures, new(.52f, .63f, .85f, .73f));
        if (_frame.StarVisibility <= 0 || (!_frame.Layers.Stars && !_frame.Layers.Labels))
            return;

        if (_frame.Layers.Labels && _frame.Selection is { } selectedId &&
            _data.Catalogue.Find(selectedId) is { } selectedStar && TryPoint(selectedId, out Vector2 selectedPoint))
            LabelStar(selectedStar, selectedPoint);

        foreach (var star in _data.Catalogue.Stars)
        {
            if (!TryStarPoint(star, out Vector2 point, out float magnitude, out float scintillation))
                continue;
            bool selected = star.Id == _frame.Selection;
            if (_frame.Layers.Stars)
            {
                float size = Math.Clamp(3.2f - magnitude * .44f, .7f, 4);
                float minimum = _frame.Conditions.HasValue ? .05f : .30f;
                float alpha = _frame.StarVisibility * Math.Clamp(scintillation * (1 - magnitude * .11f), minimum, 1);
                DrawCircle(point, size, new(.84f, .91f, 1, alpha), antialiased: true);
                if (selected)
                    DrawArc(point, 8, 0, Mathf.Tau, 20, new Color(.55f, .82f, 1), 1, true);
            }
            if (_frame.Layers.Labels && !selected && star.Name.Length > 0 && star.VisualMagnitude <= 2.5)
                LabelStar(star, point);
        }

        DrawTransients();

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

    private void DrawTransients()
    {
        foreach (var star in _data.Transients.Stars)
        {
            if (!TryTransientPoint(star, out Vector2 point, out float magnitude))
                continue;
            float radius = Math.Clamp(3.2f - .48f * magnitude, .65f, 6.5f);
            float visibility = _frame.StarVisibility * Math.Clamp(1 - .085f * magnitude, .05f, 1);
            Color tint = star.Supernova ? new(.84f, .90f, 1) : new(1, .97f, .90f);
            if (magnitude < 1.5f)
                DrawCircle(point, radius * 1.8f, new Color(tint, visibility * .22f), antialiased: true);
            DrawCircle(point, radius, new Color(tint, visibility), antialiased: true);
            if (_frame.Layers.Labels && magnitude <= 2.5f)
                DrawString(ThemeDB.FallbackFont, point + new Vector2(radius + 5, -4), star.Designation,
                    fontSize: 13, modulate: new Color(tint, visibility));
        }
    }

    private bool TryTransientPoint(TransientStar star, out Vector2 point, out float magnitude)
    {
        point = default;
        magnitude = (float)star.MagnitudeAt(_frame.Epoch.ToJulianDate());
        if (!_frame.Layers.Stars || _frame.StarVisibility <= 0 || magnitude > 14)
            return false;
        return TrySkyObject(star.Direction, magnitude, star.Direction.X * 18.1, 8, out point, out magnitude, out _);
    }

    private void DrawSegments(IReadOnlyList<(Vector3 From, Vector3 To)> lines, Color color, bool clipHorizon = true)
    {
        var rect = new Rect2(Vector2.Zero, Size).Grow(20);
        foreach (var (from, to) in lines)
        {
            Vector3 start = from, end = to;
            if (clipHorizon && _frame.Horizon is { } horizon)
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
            if (Project(start, out Vector2 a, clipHorizon) && Project(end, out Vector2 b, clipHorizon) &&
                new Rect2(a, Vector2.Zero).Expand(b).Grow(1).Intersects(rect))
                DrawLine(a, b, color, 1, true);
        }
    }

    private bool Project(Vector3 direction, out Vector2 point, bool clipHorizon = true)
    {
        point = default;
        if (clipHorizon && _frame.Horizon is { } horizon && direction.Dot(horizon) < 0)
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

    private static IEnumerable<(Vector3, Vector3)> MakeAlternateGrid(Func<float, float, Vector3> direction)
    {
        for (int longitude = 0; longitude < 360; longitude += 15)
        for (int latitude = -80; latitude < 80; latitude += 2)
            yield return (direction(longitude, latitude), direction(longitude, latitude + 2));
        for (int latitude = -80; latitude <= 80; latitude += 10)
        for (int longitude = 0; longitude < 360; longitude += 3)
            yield return (direction(longitude, latitude), direction(longitude + 3, latitude));
    }

    internal static Vector3 EclipticDirection(float longitude, float latitude)
    {
        const float obliquity = 23.4392911f * MathF.PI / 180;
        return BrightSkyData.Equatorial(longitude, latitude).Rotated(Vector3.Right, obliquity);
    }

    internal static Vector3 GalacticDirection(float longitude, float latitude)
    {
        Vector3 direction = BrightSkyData.Equatorial(longitude, latitude);
        return Convert(GalacticLight.ToEquatorial.Transform(new(direction.X, direction.Y, direction.Z)));
    }

    internal static Vector3 Convert(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);
}
