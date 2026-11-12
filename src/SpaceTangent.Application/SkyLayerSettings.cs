// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public readonly record struct SkyLayerSettings(bool Stars, bool Labels, bool EquatorialGrid, bool Constellations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeepSky { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Meteors { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CultureId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SkyCultureNames CultureNames { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConstellationArt { get; init; }

    [JsonIgnore]
    public bool HasCultureSettings => CultureId is not null || CultureNames != default || ConstellationArt;

    [JsonIgnore]
    public bool Visible => Stars || Labels || EquatorialGrid || Constellations || DeepSky || Meteors || ConstellationArt;

    internal bool IsValid() => (CultureId is null || SkyCultureLibrary.ValidId(CultureId)) && Enum.IsDefined(CultureNames);
}
