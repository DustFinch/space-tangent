// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal partial class BrightSkyOverlay
{
    private const int ArtGrid = 8;
    private readonly List<(Vector3 From, Vector3 To)> _cultureLines = [];
    private readonly Dictionary<uint, Vector3?> _cultureDirections = new();
    private readonly List<int> _artIndices = new(ArtGrid * ArtGrid * 6);
    private SimulationWorld? _cultureWorld;

    internal string? RenderedCulture { get; private set; }
    internal int RenderedArtwork { get; private set; }
    internal IReadOnlyCollection<string> RenderedCultureNames => _cultureNames;
    private readonly HashSet<string> _cultureNames = new(StringComparer.Ordinal);

    internal void SetCultureWorld(SimulationWorld world)
    {
        if (ReferenceEquals(_cultureWorld, world))
            return;
        _cultureWorld = world;
        QueueRedraw();
    }

    private bool DrawCulture(List<Rect2> occupied)
    {
        RenderedCulture = null;
        RenderedArtwork = 0;
        _cultureNames.Clear();
        _cultureDirections.Clear();
        var culture = _data.Cultures.Find(_frame.Layers.CultureId);
        if (_frame.Layers.ConstellationArt && _frame.StarVisibility > 0)
        {
            var artwork = culture ?? _data.Cultures.Find("western");
            if (artwork is not null)
                foreach (var figure in artwork.Figures)
                    if (figure.Artwork is { } image && DrawArtwork(image))
                        RenderedArtwork++;
        }
        if (culture is null || !_frame.Layers.Constellations)
            return false;
        RenderedCulture = culture.Id;
        _cultureLines.Clear();
        var labelArea = new Rect2(Vector2.Zero, Size);
        var labels = new List<(string Text, Vector2 Position, int Points)>();
        foreach (SkyCultureFigure figure in culture.Figures)
        {
            Vector3 center = default;
            int points = 0;
            foreach (var chain in figure.Lines)
            {
                Vector3? previous = null;
                foreach (uint? star in chain)
                {
                    Vector3? direction = star is { } hip ? CultureDirection(hip) : null;
                    if (direction is { } end)
                    {
                        center += end;
                        points++;
                        if (previous is { } start)
                            BrightSkyData.AddArc(start, end, _cultureLines);
                    }
                    previous = direction;
                }
            }
            if (_frame.Layers.Labels && center.LengthSquared() > 1e-10f &&
                Project(center.Normalized(), out Vector2 label) && labelArea.HasPoint(label))
            {
                labels.Add((figure.DisplayName(_frame.Layers.CultureNames), label, points));
            }
        }
        DrawSegments(_cultureLines, new(.52f, .63f, .85f, .73f));
        Font font = ThemeDB.FallbackFont;
        foreach (var (text, point, _) in labels.OrderByDescending(label => label.Points))
        {
            float width = font.GetStringSize(text, fontSize: 13).X;
            Vector2 start = point - new Vector2(width / 2, 7);
            foreach (float offset in new[] { 0f, 20f, -20f })
            {
                Vector2 baseline = start + new Vector2(0, offset);
                var bounds = new Rect2(baseline - new Vector2(0, 13), new(width, 16));
                if (!labelArea.Encloses(bounds) || occupied.Any(existing => existing.Grow(2).Intersects(bounds)))
                    continue;
                occupied.Add(bounds);
                DrawString(font, baseline, text, fontSize: 13, modulate: new Color(.75f, .84f, 1, .85f));
                _cultureNames.Add(text);
                break;
            }
        }
        return true;
    }

    private Vector3? CultureDirection(uint hip)
    {
        if (_cultureDirections.TryGetValue(hip, out var cached))
            return cached;
        ObjectId id = new(FormattableString.Invariant($"hip/{hip}"));
        Vector3d? direction = null;
        if (_cultureWorld?.Stellar.Find(id) is { IsBarycenter: false } star)
        {
            Vector3d relative = star.StateAt(_frame.Epoch).PositionKilometers - _frame.ObserverFromSun;
            if (relative.Length > 0)
                direction = relative / relative.Length;
        }
        direction ??= _data.Catalogue.Find(id)?.DirectionAt(_frame.Epoch);
        var result = direction is { } value ? Convert(value) : (Vector3?)null;
        _cultureDirections.Add(hip, result);
        return result;
    }

    private bool DrawArtwork(SkyCultureArtwork image)
    {
        if (CultureDirection(image.First.Hipparcos) is not { } a ||
            CultureDirection(image.Second.Hipparcos) is not { } b ||
            CultureDirection(image.Third.Hipparcos) is not { } c)
            return false;
        var first = new Vector3d(a.X, a.Y, a.Z);
        var second = new Vector3d(b.X, b.Y, b.Z);
        var third = new Vector3d(c.X, c.Y, c.Z);
        if ((a + b + c).Dot(_frame.Forward) < -.15f)
            return false;
        const int side = ArtGrid + 1;
        var projected = new Vector2[side * side];
        var uv = new Vector2[projected.Length];
        var valid = new bool[projected.Length];
        for (int row = 0; row < side; row++)
        for (int column = 0; column < side; column++)
        {
            int index = row * side + column;
            var position = uv[index] = new((float)column / ArtGrid, (float)row / ArtGrid);
            if (image.Direction(position.X * image.Width, position.Y * image.Height, first, second, third) is { } direction)
                valid[index] = Project(Convert(direction), out projected[index]);
        }
        _artIndices.Clear();
        var viewport = new Rect2(Vector2.Zero, Size);
        for (int row = 0; row < ArtGrid; row++)
        for (int column = 0; column < ArtGrid; column++)
        {
            int index = row * side + column;
            Triangle(index, index + 1, index + side);
            Triangle(index + 1, index + side + 1, index + side);
        }
        if (_artIndices.Count == 0 || _data.ArtTexture(image) is not { } texture)
            return false;
        Color[] colors = [new(1, 1, 1, .40f * _frame.StarVisibility)];
        RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), _artIndices.ToArray(), projected, colors, uv,
            Array.Empty<int>(), Array.Empty<float>(), texture.GetRid(), -1);
        return true;

        void Triangle(int x, int y, int z)
        {
            if (!valid[x] || !valid[y] || !valid[z] ||
                !new Rect2(projected[x], Vector2.Zero).Expand(projected[y]).Expand(projected[z]).Intersects(viewport))
                return;
            _artIndices.Add(x);
            _artIndices.Add(y);
            _artIndices.Add(z);
        }
    }
}
