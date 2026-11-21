// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.UI;

public partial class SurfaceHorizonOverlay : Control
{
    private BodySurfaceFrame _frame;
    private Vector3d _forward, _right, _up;
    private double _tangent;
    private bool _grid, _cardinals;

    public SurfaceHorizonOverlay() { MouseFilter = MouseFilterEnum.Ignore; ClipContents = true; }

    public void ShowFrame(BodySurfaceFrame frame, Vector3d forward, Vector3d right, Vector3d up,
        double fieldDegrees, bool grid, bool cardinals)
    {
        _frame = frame; _forward = forward; _right = right; _up = up;
        _tangent = Math.Tan(fieldDegrees * Math.PI / 360); _grid = grid; _cardinals = cardinals;
        Visible = grid || cardinals;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_grid)
        {
            for (int altitude = -60; altitude <= 60; altitude += 15)
                Curve(Enumerable.Range(0, 181).Select(i => _frame.LookDirection(i * 2, altitude)), altitude == 0);
            for (int azimuth = 0; azimuth < 360; azimuth += 30)
                Curve(Enumerable.Range(0, 91).Select(i => _frame.LookDirection(azimuth, i * 2 - 90)), false);
        }
        if (_cardinals)
            foreach (var (azimuth, name) in new[] { (0, "N"), (90, "E"), (180, "S"), (270, "W") })
                if (Project(_frame.LookDirection(azimuth, 0)) is { } point)
                    DrawString(ThemeDB.FallbackFont, point + new Vector2(3, -5), name, fontSize: 15, modulate: new Color("e4d5a3"));
    }

    private void Curve(IEnumerable<Vector3d> points, bool horizon)
    {
        Vector2? previous = null;
        foreach (var ray in points)
        {
            var current = Project(ray);
            if (current is { } b && previous is { } a && a.DistanceTo(b) < Size.Length() * 2)
                DrawLine(a, b, horizon ? new Color(.9f, .85f, .6f, .65f) : new Color(.65f, .75f, .9f, .28f), 1, true);
            previous = current;
        }
    }

    private Vector2? Project(Vector3d ray)
    {
        double depth = Vector3d.Dot(ray, _forward);
        if (depth <= .001 || _tangent <= 0) return null;
        double scale = Size.Y / (2 * _tangent * depth);
        return new Vector2(Size.X / 2 + (float)(Vector3d.Dot(ray, _right) * scale),
            Size.Y / 2 - (float)(Vector3d.Dot(ray, _up) * scale));
    }
}
