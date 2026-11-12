// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using System.Text.Json.Serialization;

namespace SpaceTangent.Application;

public enum CameraSensorPreset { None, ApsC, FullFrame, Asi533, Asi2600 }

public readonly record struct OpticalFinders(bool Telrad, CameraSensorPreset Sensor, bool EyepieceCircle)
{
    [JsonIgnore]
    public bool Visible => Telrad || Sensor != CameraSensorPreset.None || EyepieceCircle;
    public bool IsValid() => Enum.IsDefined(Sensor);

    [JsonIgnore]
    public (string Name, double WidthMillimeters, double HeightMillimeters) SensorDimensions => Sensor switch
    {
        CameraSensorPreset.ApsC => ("APS-C", 23.5, 15.6),
        CameraSensorPreset.FullFrame => ("Full frame", 36, 24),
        CameraSensorPreset.Asi533 => ("ASI533", 11.31, 11.31),
        CameraSensorPreset.Asi2600 => ("ASI2600", 23.5, 15.7),
        _ => ("", 0, 0)
    };

    public (double Width, double Height) SensorFieldDegrees(VisualOptics profile)
    {
        var (_, width, height) = SensorDimensions;
        double focal = profile.Tube.FocalMillimeters * profile.Barlow;
        return (Math.Atan(width / (2 * focal)) * 360 / Math.PI, Math.Atan(height / (2 * focal)) * 360 / Math.PI);
    }

    public static double HalfHeightFraction(double fieldDegrees, double viewDegrees) =>
        Math.Tan(fieldDegrees * Math.PI / 360) / Math.Tan(viewDegrees * Math.PI / 360);
}
