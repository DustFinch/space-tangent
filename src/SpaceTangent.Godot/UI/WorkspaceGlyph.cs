// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.UI;

internal partial class WorkspaceGlyph : Control
{
    internal string Page { get; init; } = "";

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        Vector2 origin = Size / 2 - new Vector2(12, 12);
        Color ink = SimulatorTheme.Ink;
        switch (Page)
        {
            case "Search":
                Ring(10, 10, 5); Path((14, 14), (19, 19));
                break;
            case "Time":
                Ring(12, 12, 7); Path((12, 7), (12, 12), (16, 14));
                break;
            case "Layers":
                Path((4, 10), (12, 6), (20, 10), (12, 14), (4, 10));
                Path((4, 14), (12, 18), (20, 14));
                break;
            case "Location":
                DrawArc(origin + new Vector2(12, 9), 5, Mathf.Pi, Mathf.Tau, 20, ink, 1.1f, true);
                Path((7, 9), (8, 13), (12, 19), (16, 13), (17, 9)); Ring(12, 9, 1.6f);
                break;
            case "Ephemerides":
                DrawArc(origin + new Vector2(12, 16), 4, Mathf.Pi, Mathf.Tau, 20, ink, 1.1f, true);
                Path((4, 17), (20, 17)); Path((7, 20), (17, 20));
                Path((12, 5), (12, 9)); Path((5, 9), (7, 11)); Path((19, 9), (17, 11));
                break;
            case "Optics":
                Path((5, 11), (17, 5), (19, 9), (7, 15), (5, 11));
                Path((12, 13), (12, 16), (9, 20)); Path((12, 16), (15, 20));
                break;
            case "SkyCultures":
                Path((5, 16), (9, 9), (14, 13), (19, 6));
                foreach (var (x, y) in new[] { (5, 16), (9, 9), (14, 13), (19, 6) })
                    DrawCircle(origin + new Vector2(x, y), 1.65f, ink, true, -1, true);
                break;
            case "Bookmarks":
                Path((7, 5), (17, 5), (17, 19), (12, 15), (7, 19), (7, 5));
                break;
            case "AddOns":
                Path((5, 8), (12, 4), (19, 8), (19, 16), (12, 20), (5, 16), (5, 8), (12, 12), (19, 8));
                Path((12, 12), (12, 20));
                break;
            case "Scripts":
                Path((7, 4), (14, 4), (18, 8), (18, 20), (7, 20), (7, 4));
                Path((14, 4), (14, 8), (18, 8)); Path((11, 11), (15, 14), (11, 17), (11, 11));
                break;
            case "Ship":
                Path((12, 4), (15, 10), (16, 14), (19, 18), (14, 17), (12, 20), (10, 17), (5, 18), (8, 14), (9, 10), (12, 4));
                break;
            case "Settings":
                Path((5, 7), (19, 7)); Path((5, 12), (19, 12)); Path((5, 17), (19, 17));
                Path((9, 5), (9, 9)); Path((15, 10), (15, 14)); Path((11, 15), (11, 19));
                break;
            case "Help":
                Ring(12, 12, 7); Path((10, 9), (11, 8), (13, 8), (14, 9), (14, 10), (12, 12), (12, 13));
                DrawCircle(origin + new Vector2(12, 16), .8f, ink, true, -1, true);
                break;
        }

        void Path(params (float X, float Y)[] points) =>
            DrawPolyline(points.Select(point => origin + new Vector2(point.X, point.Y)).ToArray(), ink, 1.1f, true);

        void Ring(float x, float y, float radius) =>
            DrawArc(origin + new Vector2(x, y), radius, 0, Mathf.Tau, 28, ink, 1.1f, true);
    }
}
