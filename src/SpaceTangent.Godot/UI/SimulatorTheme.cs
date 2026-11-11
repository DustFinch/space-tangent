// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.UI;

internal static class SimulatorTheme
{
    internal static readonly Color Ground = new("07090c");
    internal static readonly Color Rail = new("1d2d3d");
    internal static readonly Color Raised = new("2c455d");
    internal static readonly Color Ink = new("f2f2f3");
    internal static readonly Color Accent = new("5980a6");
    internal static readonly Color Highlight = new("94bce3");
    internal static readonly Color Border = new(Ink, .16f);
    internal static readonly Color Muted = new(Ink, .64f);

    internal static Theme Create()
    {
        var body = GD.Load<FontFile>("res://UI/Fonts/FiraSans-Regular.ttf");
        var heading = GD.Load<FontFile>("res://UI/Fonts/FiraSansCondensed-SemiBold.ttf");
        var theme = new Theme { DefaultFont = body, DefaultFontSize = 14 };

        foreach (string type in new[] { "Button", "CheckButton", "LineEdit", "PopupMenu", "Label" })
            theme.SetColor("font_color", type, Ink);

        foreach (string type in new[] { "Button", "OptionButton", "MenuButton" })
        {
            theme.SetFont("font", type, heading);
            theme.SetStylebox("normal", type, Box(Colors.Transparent, Border, 8, 5));
            theme.SetStylebox("hover", type, Box(Raised, Highlight, 8, 5));
            theme.SetStylebox("pressed", type, Box(Accent, Highlight, 8, 5));
            theme.SetStylebox("hover_pressed", type, Box(Accent, Highlight, 8, 5));
            theme.SetStylebox("disabled", type, Box(Colors.Transparent, Border, 8, 5));
            theme.SetStylebox("focus", type, Box(Colors.Transparent, Highlight, 8, 5));
            theme.SetColor("font_hover_color", type, Ink);
            theme.SetColor("font_pressed_color", type, Ink);
            theme.SetColor("font_disabled_color", type, Muted);
        }

        theme.SetTypeVariation("RailButton", "Button");
        theme.SetStylebox("normal", "RailButton", Box(Colors.Transparent, Colors.Transparent, 4, 3));
        theme.SetStylebox("hover", "RailButton", Box(Raised, Colors.Transparent, 4, 3));
        theme.SetStylebox("pressed", "RailButton", Box(Raised, Highlight, 4, 3));
        theme.SetStylebox("hover_pressed", "RailButton", Box(Raised, Highlight, 4, 3));

        theme.SetStylebox("normal", "LineEdit", Box(new(Ink, .05f), Border, 8, 6));
        theme.SetStylebox("focus", "LineEdit", Box(Colors.Transparent, Highlight, 8, 6));
        theme.SetColor("font_placeholder_color", "LineEdit", Muted);
        theme.SetStylebox("panel", "PopupMenu", Box(Rail, Border, 4, 4));
        theme.SetStylebox("hover", "PopupMenu", Box(Raised, Colors.Transparent, 6, 3));
        theme.SetStylebox("panel", "PanelContainer", Box(Rail, Border, 0, 0));
        theme.SetStylebox("panel", "TooltipPanel", Box(Rail, Border, 6, 4));
        theme.SetTypeVariation("WorkspaceTitle", "Label");
        theme.SetFont("font", "WorkspaceTitle", heading);
        theme.SetFontSize("font_size", "WorkspaceTitle", 21);
        theme.SetTypeVariation("WorkspaceSection", "Label");
        theme.SetFont("font", "WorkspaceSection", heading);
        theme.SetFontSize("font_size", "WorkspaceSection", 12);
        theme.SetColor("font_color", "WorkspaceSection", Muted);
        theme.SetTypeVariation("WorkspaceDetail", "Label");
        theme.SetColor("font_color", "WorkspaceDetail", Muted);
        theme.SetFontSize("font_size", "WorkspaceDetail", 12);
        return theme;
    }

    internal static StyleBoxFlat Box(Color fill, Color edge, float horizontal, float vertical) => new()
    {
        BgColor = fill, BorderColor = edge,
        BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
        ContentMarginLeft = horizontal, ContentMarginRight = horizontal,
        ContentMarginTop = vertical, ContentMarginBottom = vertical
    };

    internal static Label Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), ThemeTypeVariation = "WorkspaceSection",
        MouseFilter = Control.MouseFilterEnum.Ignore
    };

    internal static Label Detail(string text) => new()
    {
        Text = text, ThemeTypeVariation = "WorkspaceDetail",
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        MouseFilter = Control.MouseFilterEnum.Ignore
    };
}
