// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private Label? _scriptNoticeLabel;
    private ScriptNotice? _notice;
    internal string? VisibleScriptNotice => _scriptNoticeLabel is { Visible: true } label ? label.Text : null;

    internal void SetScriptNotice(ScriptNotice? notice)
    {
        if (_notice == notice)
            return;
        _notice = notice;
        if (notice is null)
        {
            _scriptNoticeLabel?.Hide();
            return;
        }
        if (_scriptNoticeLabel is null)
        {
            var layer = new CanvasLayer { Layer = 26 };
            AddChild(layer);
            _scriptNoticeLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
            _scriptNoticeLabel.AddThemeConstantOverride("outline_size", 4);
            _scriptNoticeLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, .9f));
            layer.AddChild(_scriptNoticeLabel);
            GetViewport().SizeChanged += PlaceScriptNotice;
        }
        _scriptNoticeLabel.Text = notice.Value.Text;
        _scriptNoticeLabel.Show();
        PlaceScriptNotice();
    }

    private void PlaceScriptNotice()
    {
        if (_notice is not { } notice || _scriptNoticeLabel is not { } label)
            return;
        var font = label.GetThemeFont("font");
        int fontSize = label.GetThemeFontSize("font_size");
        int line = Math.Max(1, Mathf.RoundToInt(font.GetHeight(fontSize)));
        int em = Math.Max(1, Mathf.RoundToInt(font.GetStringSize("M", fontSize: fontSize).X));
        var size = GetViewport().GetVisibleRect().Size;
        var (x, y) = notice.Cursor((int)size.X, (int)size.Y, em, line);
        label.Position = new((float)x, size.Y - (float)y - font.GetAscent(fontSize));
        label.Size = label.GetMinimumSize();
    }
}
