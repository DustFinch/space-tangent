// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.UI;

internal static class WorkspacePanelLayout
{
    internal static void Optics(VBoxContainer page)
    {
        page.AddChild(SimulatorTheme.Section("Telescope"));
        DisabledField(page, "Choose a telescope");
        DisabledSegments(page, "Add telescope", "Remove");
        page.AddChild(SimulatorTheme.Section("Eyepiece"));
        DisabledField(page, "Choose an eyepiece");
        DisabledSegments(page, "Add eyepiece", "Remove");
        page.AddChild(SimulatorTheme.Section("Barlow lens"));
        DisabledSegments(page, "None", "×2", "×3");
        page.AddChild(SimulatorTheme.Section("Atmospheric seeing"));
        DisabledSegments(page, "None", "1″", "2″", "3″", "5″");
        var metrics = new GridContainer { Columns = 2 };
        foreach (string label in new[] { "Magnification", "True field", "Exit pupil", "Limiting magnitude", "Resolution", "Star blur" })
        {
            metrics.AddChild(SimulatorTheme.Detail(label));
            metrics.AddChild(new Label { Text = "—", HorizontalAlignment = HorizontalAlignment.Right });
        }
        page.AddChild(metrics);
        Unavailable(page, "Eyepiece view");
    }

    internal static void SkyCultures(VBoxContainer page)
    {
        page.AddChild(SimulatorTheme.Section("Sky culture"));
        DisabledField(page, "Choose a sky culture");
        page.AddChild(SimulatorTheme.Section("Names"));
        DisabledSegments(page, "Local", "International");
        page.AddChild(SimulatorTheme.Section("Show"));
        DisabledChecks(page, "Constellation lines", "IAU boundaries", "Illustrations");
        Unavailable(page, "Sky culture catalogues");
    }

    internal static void Ship(VBoxContainer page)
    {
        page.AddChild(SimulatorTheme.Section("Flight controls"));
        DisabledSegments(page, "Cockpit", "Chase");
        DisabledField(page, "Throttle");
        page.AddChild(SimulatorTheme.Section("Autopilot"));
        DisabledSegments(page, "Approach", "Orbit", "Stop");
        Unavailable(page, "Ship and autopilot");
    }

    internal static void CameraSettings(VBoxContainer page)
    {
        page.AddChild(SimulatorTheme.Section("Camera"));
        DisabledChecks(page, "Inertia", "Auto-orbit");
        DisabledField(page, "Auto-orbit period");
        DisabledSegments(page, "Counterclockwise", "Clockwise");
        DisabledField(page, "Default field of view");
        DisabledField(page, "Mouse sensitivity");
        page.AddChild(SimulatorTheme.Section("Scene"));
        DisabledChecks(page, "Atmosphere", "Clouds", "Terrain", "Refraction");
        Unavailable(page, "Camera and scene preferences");
    }

    internal static void DisabledChecks(Container page, params string[] labels)
    {
        var grid = new GridContainer { Columns = 2 };
        foreach (string label in labels)
            grid.AddChild(new CheckBox { Text = label, Disabled = true });
        page.AddChild(grid);
    }

    internal static void DisabledField(Container page, string placeholder) =>
        page.AddChild(new LineEdit { PlaceholderText = placeholder, Editable = false, FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

    internal static void DisabledSegments(Container page, params string[] labels)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 0);
        foreach (string label in labels)
            row.AddChild(new Button { Text = label, Disabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        page.AddChild(row);
    }

    private static void Unavailable(Container page, string feature) =>
        page.AddChild(SimulatorTheme.Detail($"{feature} is unavailable in this build."));
}
