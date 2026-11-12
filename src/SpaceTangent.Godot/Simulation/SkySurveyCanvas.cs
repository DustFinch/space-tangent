// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed class SkySurveyTiles
{
    private const int MaximumTiles = 220, MaximumLookups = 4096, MaximumPixels = 32 * 1024 * 1024;
    private readonly Dictionary<string, LocalSkySurvey> _surveys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Texture2D Texture, ulong Used)> _tiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private int _pixels;
    private ulong _use;

    internal IReadOnlyList<LocalSkyPhotograph> Photographs { get; } = [];

    internal SkySurveyTiles(string root, string photographs)
    {
        if (File.Exists(Path.Combine(photographs, "textures.json")))
        {
            try { Photographs = LocalSkyPhotographs.Read(photographs); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                GD.Print($"Local sky photographs: {error.Message}");
            }
        }

        LoadSurveys(root);
    }

    private void LoadSurveys(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).Take(64))
        {
            if (!File.Exists(Path.Combine(directory, "properties")))
                continue;
            try
            {
                var survey = LocalSkySurvey.Read(directory);
                _surveys.Add(survey.Id, survey);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                GD.Print($"Local sky survey {Path.GetFileName(directory)}: {error.Message}");
            }
        }
    }

    internal LocalSkySurvey? Find(string? id) => id is null ? null : _surveys.GetValueOrDefault(id);

    internal Texture2D? Texture(string path, LocalSkySurvey? survey, bool allSky = false)
    {
        if (_tiles.TryGetValue(path, out var cached))
        {
            _tiles[path] = (cached.Texture, ++_use);
            return cached.Texture;
        }
        if (_missing.Contains(path))
            return null;
        try
        {
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                if (file.Length is < 16 or > 16 * 1024 * 1024)
                    throw new InvalidDataException("A sky survey image exceeds its size limit.");
                var bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
                using var image = new Image();
                if (survey is null && !ValidPhotographHeader(bytes))
                    throw new InvalidDataException("A sky photograph has an unsupported PNG header.");
                Error status = survey is null || survey.Extension == "png" ? image.LoadPngFromBuffer(bytes) : image.LoadJpgFromBuffer(bytes);
                int width = image.GetWidth(), height = image.GetHeight();
                if (status != Error.Ok || width is < 2 or > 4096 || height is < 2 or > 4096 ||
                    (survey is not null && (allSky ? !LocalSkySurvey.ValidAllSky(width, height) : width != height || width != survey.TileWidth)))
                    throw new InvalidDataException("A sky survey image has unsupported dimensions.");
                int pixels = width * height;
                while (_tiles.Count >= MaximumTiles || _pixels + pixels > MaximumPixels)
                {
                    var oldest = _tiles.MinBy(pair => pair.Value.Used);
                    _pixels -= oldest.Value.Texture.GetWidth() * oldest.Value.Texture.GetHeight();
                    _tiles.Remove(oldest.Key);
                }
                var texture = ImageTexture.CreateFromImage(image);
                _tiles[path] = (texture, ++_use);
                _pixels += pixels;
                return texture;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.Print($"Local sky survey image {Path.GetFileName(path)}: {error.Message}");
        }
        if (_missing.Count >= MaximumLookups)
            _missing.Clear();
        _missing.Add(path);
        return null;
    }

    private static bool ValidPhotographHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8))
            return false;
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        return width is >= 2 and <= 4096 && height is >= 2 and <= 4096;
    }
}

internal partial class SkySurveyCanvas : Control
{
    private readonly record struct Frame(LocalSkySurvey Survey, Texture2D AllSky, Vector3 Forward,
        Vector3 Right, Vector3 Up, float Tangent, float Brightness, bool Background);

    private sealed class Batch
    {
        internal readonly List<int> Indices = [];
        internal readonly List<Vector2> Points = [], Uvs = [];
    }

    private readonly SkySurveyTiles _tiles;
    private readonly List<Texture2D> _drawnTextures = [];
    private Frame? _frame;
    private readonly bool _alwaysAdditive;
    private readonly CanvasItemMaterial _additive = new() { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };

    internal SkySurveyCanvas(SkySurveyTiles tiles, bool additive)
    {
        _tiles = tiles;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        Visible = false;
        TextureFilter = TextureFilterEnum.Linear;
        _alwaysAdditive = additive;
        if (additive)
            Material = _additive;
        Resized += QueueRedraw;
    }

    internal int DetailPatches { get; private set; }
    internal int OverviewPatches { get; private set; }
    internal int FinestSourceOrder { get; private set; }
    internal string? SurveyId => _frame?.Survey.Id;

    internal bool ShowFrame(string? id, Vector3 forward, Vector3 right, Vector3 up,
        float tangent, float brightness = 1, bool background = false, bool photographs = false,
        double limitingMagnitude = 7.6, double julianDate = 2451545)
    {
        Frame? next = null;
        if (tangent > 0 && brightness > 0 && _tiles.Find(id) is { } survey &&
            _tiles.Texture(survey.AllSkyPath, survey, allSky: true) is { } allSky)
            next = new(survey, allSky, forward, right, up, tangent, brightness, background);
        PhotographFrame? photoFrame = next is null && photographs && tangent > 0 && _tiles.Photographs.Count != 0 ?
            new(forward, right, up, tangent, limitingMagnitude, julianDate) : null;
        if (_frame != next || _photographs != photoFrame)
        {
            _frame = next;
            _photographs = photoFrame;
            Material = _alwaysAdditive || photoFrame is not null ? _additive : null;
            Visible = next is not null || photoFrame is not null;
            if (next is null)
                DetailPatches = OverviewPatches = FinestSourceOrder = 0;
            if (photoFrame is null)
                _drawnPhotographs.Clear();
            QueueRedraw();
        }
        return Visible;
    }

    public override void _Draw()
    {
        _drawnTextures.Clear();
        DetailPatches = OverviewPatches = FinestSourceOrder = 0;
        _drawnPhotographs.Clear();
        if (Size.X <= 1 || Size.Y <= 1)
            return;
        if (_photographs is { } photographs)
        {
            DrawPhotographs(photographs);
            return;
        }
        if (_frame is not { } frame)
            return;
        if (frame.Background)
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(.022f, .026f, .043f));

        var observer = new Vector3d(frame.Forward.X, frame.Forward.Y, frame.Forward.Z);
        Vector3d axis = frame.Survey.ToEquatorial.Transpose().Transform(observer);
        double aspect = Size.X / Size.Y;
        double diagonal = Math.Atan(frame.Tangent * Math.Sqrt(1 + aspect * aspect)) * 1.04;
        int order = frame.Survey.OrderFor(Math.Atan(frame.Tangent) * 360 / Math.PI, (int)Size.Y);
        IReadOnlyList<long> visible = HealpixSkyGrid.Cover(order, axis, diagonal, LocalSkySurvey.MaximumViewCells);
        while (visible.Count > LocalSkySurvey.MaximumViewCells && order > LocalSkySurvey.AllSkyOrder)
            visible = HealpixSkyGrid.Cover(--order, axis, diagonal, LocalSkySurvey.MaximumViewCells);
        if (visible.Count > LocalSkySurvey.MaximumViewCells)
            visible = HealpixSkyGrid.Cover(order, axis, diagonal, (int)HealpixSkyGrid.Count(order));

        int steps = order <= 3 ? 5 : order <= 5 ? 3 : 2;
        int width = steps + 1;
        Span<Vector2> points = stackalloc Vector2[36];
        Span<Vector2> coordinates = stackalloc Vector2[36];
        Span<bool> projected = stackalloc bool[36];
        var batches = new Dictionary<Texture2D, Batch>();
        var viewport = new Rect2(Vector2.Zero, Size);
        foreach (long cell in visible)
        {
            Texture2D source = frame.AllSky;
            int sourceOrder = LocalSkySurvey.AllSkyOrder;
            for (int level = order; level > LocalSkySurvey.AllSkyOrder; level--)
            {
                long parent = cell >> (2 * (order - level));
                if (_tiles.Texture(frame.Survey.TilePath(level, parent), frame.Survey) is { } texture)
                {
                    source = texture;
                    sourceOrder = level;
                    break;
                }
            }
            bool overview = sourceOrder == LocalSkySurvey.AllSkyOrder;
            int sourceWidth = source.GetWidth(), sourceHeight = source.GetHeight();
            for (int i = 0; i < width; i++)
            for (int j = 0; j < width; j++)
            {
                int at = i * width + j;
                double x = i / (double)steps, y = j / (double)steps;
                Vector3d direction = frame.Survey.ToEquatorial.Transform(HealpixSkyGrid.Direction(order, cell, x, y));
                var sight = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z);
                float depth = sight.Dot(frame.Forward);
                projected[at] = depth > .01f;
                points[at] = projected[at] ? Size / 2 + new Vector2(sight.Dot(frame.Right), -sight.Dot(frame.Up)) *
                    (Size.Y / (2 * frame.Tangent * depth)) : default;
                var (u, v) = overview ? LocalSkySurvey.AllSkyUv(order, cell, sourceWidth, sourceHeight, x, y) :
                    LocalSkySurvey.TileUv(order, cell, sourceOrder, sourceWidth, x, y);
                coordinates[at] = new((float)u, (float)v);
            }
            if (!batches.TryGetValue(source, out Batch? batch))
                batches[source] = batch = new();
            int before = batch.Indices.Count;
            int first = batch.Points.Count;
            for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                int a = i * width + j, b = a + width;
                AddTriangle(a, b, a + 1, projected, points, first, viewport, batch);
                AddTriangle(a + 1, b, b + 1, projected, points, first, viewport, batch);
            }
            if (batch.Indices.Count == before)
                continue;
            for (int i = 0; i < width * width; i++)
            {
                batch.Points.Add(points[i]);
                batch.Uvs.Add(coordinates[i]);
            }
            if (overview)
                OverviewPatches++;
            else
                DetailPatches++;
            FinestSourceOrder = Math.Max(FinestSourceOrder, sourceOrder);
        }

        Color[] colors = [new(frame.Brightness, frame.Brightness, frame.Brightness, 1)];
        foreach (var (texture, batch) in batches)
        {
            if (batch.Indices.Count == 0)
                continue;
            _drawnTextures.Add(texture);
            RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), batch.Indices.ToArray(), batch.Points.ToArray(),
                colors, batch.Uvs.ToArray(), Array.Empty<int>(), Array.Empty<float>(), texture.GetRid(), -1);
        }
    }

    private static void AddTriangle(int a, int b, int c, ReadOnlySpan<bool> valid, ReadOnlySpan<Vector2> points,
        int first, Rect2 viewport, Batch batch)
    {
        if (!valid[a] || !valid[b] || !valid[c] ||
            !new Rect2(points[a], Vector2.Zero).Expand(points[b]).Expand(points[c]).Intersects(viewport))
            return;
        batch.Indices.Add(first + a);
        batch.Indices.Add(first + b);
        batch.Indices.Add(first + c);
    }
}
