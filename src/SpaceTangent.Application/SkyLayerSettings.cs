// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;

namespace SpaceTangent.Application;

public readonly record struct SkyLayerSettings(bool Stars, bool Labels, bool EquatorialGrid, bool Constellations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeepSky { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Meteors { get; init; }

    [JsonIgnore]
    public bool Visible => Stars || Labels || EquatorialGrid || Constellations || DeepSky || Meteors;
}
