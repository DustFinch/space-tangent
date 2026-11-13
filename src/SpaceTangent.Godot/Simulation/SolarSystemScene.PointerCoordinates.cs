// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private void UpdatePointerReadout(ViewControls controls, SkyLayerSettings sky)
    {
        if (!sky.PointerCoordinates || controls.PointerPosition is not { } at || Input.MouseMode == Input.MouseModeEnum.Captured ||
            !new Rect2(Vector2.Zero, controls.Canvas.Size).HasPoint(at) ||
            controls.Renderer.PointerDirection(at) is not { } pointer)
        {
            controls.PointerReadout?.Hide();
            return;
        }

        const double degrees = 180 / Math.PI;
        double ra = (Math.Atan2(pointer.Ray.Y, pointer.Ray.X) * degrees + 360) % 360;
        double dec = Math.Asin(Math.Clamp(pointer.Ray.Z, -1, 1)) * degrees;
        int tenths = (int)Math.Round(ra * 2400) % 864000;
        string hours = FormattableString.Invariant($"{tenths / 36000:00}h{tenths / 600 % 60:00}m{tenths % 600 / 10d:00.0}s");
        string text = $"α {hours}  δ {PointerAngle(dec, signed: true)} (J2000)";
        if (_brightSky?.BoundaryMap?.At(pointer.Ray) is { } abbreviation)
        {
            var figures = _brightSky.Cultures.Find(sky.CultureId ?? "western")?.Figures;
            string id = abbreviation is "SER1" or "SER2" ? "Ser" : abbreviation;
            var figure = figures?.FirstOrDefault(item => item.Id.EndsWith(" " + id, StringComparison.OrdinalIgnoreCase));
            text += "  · " + (figure?.DisplayName(sky.CultureNames) ?? abbreviation);
        }
        if (pointer.Horizon is { } horizon)
        {
            string azimuth = horizon.AzimuthDegrees is { } az ? PointerAngle(az, signed: false) : "—";
            text += $"  A {azimuth}  h {PointerAngle(horizon.AltitudeDegrees, signed: true)}";
        }
        if (controls.PointerReadout is null)
        {
            var label = new Label { Name = "PointerCoordinates", MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeFontSizeOverride("font_size", 13);
            label.AddThemeColorOverride("font_color", new Color("#b3e3fc"));
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            label.AddThemeConstantOverride("outline_size", 4);
            controls.Canvas.AddChild(label);
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
            label.OffsetLeft = 8;
            label.OffsetTop = -24;
            controls.PointerReadout = label;
        }
        controls.PointerReadout.Text = text;
        controls.PointerReadout.Show();
    }

    private static string PointerAngle(double degrees, bool signed)
    {
        int arcSeconds = (int)Math.Round(Math.Abs(degrees) * 3600);
        string sign = signed ? degrees < 0 ? "−" : "+" : "";
        if (!signed)
            arcSeconds %= 360 * 3600;
        return FormattableString.Invariant($"{sign}{arcSeconds / 3600}°{arcSeconds / 60 % 60:00}′{arcSeconds % 60:00}″");
    }
}
