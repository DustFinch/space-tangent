// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SkySurveyCanvas
{
    private readonly record struct PhotographFrame(Vector3 Forward, Vector3 Right, Vector3 Up,
        float Tangent, double LimitingMagnitude, double JulianDate);
    private PhotographFrame? _photographs;
    private readonly HashSet<string> _drawnPhotographs = new(StringComparer.OrdinalIgnoreCase);
    internal bool PhotographVisible(string file) => _drawnPhotographs.Contains(file);

    private void DrawPhotographs(PhotographFrame frame)
    {
        const int steps = 4, width = steps + 1;
        const int maximumImages = 80;
        double aspect = Size.X / Size.Y;
        double diagonal = Math.Atan(frame.Tangent * Math.Sqrt(1 + aspect * aspect));
        double pixel = 2 * Math.Atan(frame.Tangent) * 180 / (Math.PI * Size.Y);
        var center = new Vector3d(frame.Forward.X, frame.Forward.Y, frame.Forward.Z);
        var viewport = new Rect2(Vector2.Zero, Size);
        Span<Vector2> points = stackalloc Vector2[width * width];
        Span<Vector2> coordinates = stackalloc Vector2[width * width];
        Span<bool> projected = stackalloc bool[width * width];
        foreach (LocalSkyPhotograph image in _tiles.Photographs)
        {
            double opacity = image.Opacity(pixel, frame.LimitingMagnitude, frame.JulianDate);
            if (opacity <= 0 || Vector3d.Dot(image.Center, center) < Math.Cos(Math.Min(Math.PI, diagonal + image.RadiusRadians)))
                continue;
            for (int i = 0; i < width; i++)
            for (int j = 0; j < width; j++)
            {
                int at = i * width + j;
                double x = i / (double)steps, y = j / (double)steps;
                double w0 = (1 - x) * (1 - y), w1 = x * (1 - y), w2 = x * y, w3 = (1 - x) * y;
                Vector3d direction = image.Corners[0] * w0 + image.Corners[1] * w1 + image.Corners[2] * w2 + image.Corners[3] * w3;
                var sight = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z);
                float depth = sight.Dot(frame.Forward);
                projected[at] = depth > .01f;
                points[at] = projected[at] ? Size / 2 + new Vector2(sight.Dot(frame.Right), -sight.Dot(frame.Up)) *
                    (Size.Y / (2 * frame.Tangent * depth)) : default;
                var t = image.Uvs;
                coordinates[at] = new((float)(t[0].U * w0 + t[1].U * w1 + t[2].U * w2 + t[3].U * w3),
                    (float)(t[0].V * w0 + t[1].V * w1 + t[2].V * w2 + t[3].V * w3));
            }
            var batch = new Batch();
            for (int i = 0; i < steps; i++)
            for (int j = 0; j < steps; j++)
            {
                int a = i * width + j, b = a + width;
                AddTriangle(a, b, a + 1, projected, points, 0, viewport, batch);
                AddTriangle(a + 1, b, b + 1, projected, points, 0, viewport, batch);
            }
            if (batch.Indices.Count == 0 || _tiles.Texture(image.Path, null) is not { } texture)
                continue;
            _drawnTextures.Add(texture);
            _drawnPhotographs.Add(Path.GetFileName(image.Path));
            Color[] color = [new(1, 1, 1, (float)opacity)];
            RenderingServer.CanvasItemAddTriangleArray(GetCanvasItem(), batch.Indices.ToArray(), points.ToArray(),
                color, coordinates.ToArray(), Array.Empty<int>(), Array.Empty<float>(), texture.GetRid(), -1);
            if (_drawnTextures.Count == maximumImages)
                break;
        }
    }
}
