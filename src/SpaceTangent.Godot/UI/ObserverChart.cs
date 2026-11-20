// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.UI;

/// <summary>A small accessible line chart; unavailable samples interrupt the curve.</summary>
public partial class ObserverChart : Control
{
    private double?[][] _series = [];
    private double?[]? _shade;
    private bool?[]? _visible;
    private double _low = -90, _high = 90;
    private static readonly Color[] Colors = [new("73caff"), new("ffd16b"), new("c7cddd")];
    public event Action<int>? SampleChosen;

    public ObserverChart()
    {
        CustomMinimumSize = new(0, 145);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        Resized += QueueRedraw;
    }

    public void SetData(double low, double high, double?[][] series, double?[]? sunShade = null, bool?[]? visible = null)
    {
        _low = low; _high = high; _series = series; _shade = sunShade; _visible = visible;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click &&
            _series.FirstOrDefault()?.Length is > 0 and var count)
        {
            double x = Math.Clamp((click.Position.X - 27) / Math.Max(1, Size.X - 35), 0, 1);
            SampleChosen?.Invoke((int)Math.Round(x * (count - 1)));
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        float left = 27, top = 8, width = Math.Max(1, Size.X - 35), height = Math.Max(1, Size.Y - 22);
        var background = new Rect2(left, top, width, height);
        DrawRect(background, new Color("111c2d"));
        if (_shade is { Length: > 1 })
            for (int i = 0; i < _shade.Length; i++)
            {
                if (_shade[i] is not { } sun) continue;
                float alpha = (float)Math.Clamp((sun + 18) / 36, 0, 1) * .22f;
                DrawRect(new(left + width * i / _shade.Length, top, width / _shade.Length + 1, height), new Color(.8f, .58f, .2f, alpha));
            }
        var font = ThemeDB.FallbackFont;
        foreach (double value in new[] { _low, (_low + _high) / 2, _high })
        {
            float y = Y(value);
            DrawLine(new(left, y), new(left + width, y), new Color(.4f, .5f, .65f, .22f));
            DrawString(font, new(0, y + 4), value.ToString("0", System.Globalization.CultureInfo.InvariantCulture), fontSize: 10,
                modulate: new Color("9eb1ca"));
        }
        if (_low < 0 && _high > 0) DrawLine(new(left, Y(0)), new(left + width, Y(0)), new Color(.6f, .7f, .8f, .4f));
        for (int line = 0; line < _series.Length; line++)
        {
            var data = _series[line];
            for (int i = 1; i < data.Length; i++)
                if (data[i - 1] is { } a && data[i] is { } b && double.IsFinite(a) && double.IsFinite(b))
                    DrawLine(new(left + width * (i - 1) / (data.Length - 1), Y(a)),
                        new(left + width * i / (data.Length - 1), Y(b)), Colors[line % Colors.Length], 1.4f, true);
        }
        if (_visible is { Length: > 0 })
            for (int i = 0; i < _visible.Length; i++)
                DrawRect(new(left + width * i / _visible.Length, top + height + 3, width / _visible.Length + .5f, 4),
                    _visible[i] is true ? new Color("65b997") : _visible[i] is false ? new Color("35435a") : new Color("806d70"));

        float Y(double value) => top + height * (1 - (float)((Math.Clamp(value, _low, _high) - _low) / (_high - _low)));
    }
}
