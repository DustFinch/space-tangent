// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class FinderOverlay : Control
{
    private static readonly double[] RingDiameters = [.5, 2, 4];
    private static readonly Color RingColor = new(1, .35f, .3f, .75f);
    private static readonly Color SensorColor = new(.5f, 1, .7f, .8f);
    private static readonly Color EyepieceColor = new(.55f, .83f, 1, .7f);
    private ObserverOptics? _optics;
    private double _field;

    internal FinderOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        Resized += QueueRedraw;
    }

    internal void ShowFrame(ObserverOptics optics, double field)
    {
        Visible = true;
        if (_optics == optics && _field == field)
            return;
        _optics = optics;
        _field = field;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_optics is not { Finders: { } finders } optics || Size.Y < 2)
            return;
        Vector2 center = Size / 2;
        float Scale(double field) => (float)(center.Y * OpticalFinders.HalfHeightFraction(field, _field));
        if (finders.Telrad)
            foreach (double diameter in RingDiameters)
                Arc(diameter, RingColor, 1.2f);
        if (finders.EyepieceCircle)
            Arc(optics.Profile.TrueFieldDegrees(), EyepieceColor, 1);
        if (finders.Sensor == CameraSensorPreset.None)
            return;
        var (width, height) = finders.SensorFieldDegrees(optics.Profile);
        var half = new Vector2(Scale(width), Scale(height));
        DrawRect(new(center - half, half * 2), SensorColor, false, 1.2f, true);
        if (half.X >= 10 && half.Y >= 10)
            DrawString(ThemeDB.FallbackFont, center + new Vector2(-half.X, -half.Y - 6),
                FormattableString.Invariant($"{finders.SensorDimensions.Name} · {width:F2}° × {height:F2}°"),
                HorizontalAlignment.Left, -1, 12, SensorColor);

        void Arc(double field, Color color, float stroke)
        {
            float radius = Scale(field);
            if (radius >= 2 && radius < Size.Length())
                DrawArc(center, radius, 0, Mathf.Tau, 128, color, stroke, true);
        }
    }
}
