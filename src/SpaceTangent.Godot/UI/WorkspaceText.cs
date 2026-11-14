// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.UI;

internal static class WorkspaceText
{
    private static readonly bool Russian = TranslationServer.GetLocale().Split('_', '-')[0] == "ru";

    internal static string Choose(string english, string russian) => Russian ? russian : english;
}
