// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public readonly record struct SkyLayerSettings(bool Stars, bool Labels, bool EquatorialGrid, bool Constellations)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeepSky { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeepSkyVisibility? DeepSkyFilter { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? OpenClusterLabels { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NebulaLabels { get; init; }
    [JsonIgnore]
    internal bool HasDeepSkyPreferences => DeepSkyFilter.HasValue || OpenClusterLabels.HasValue || NebulaLabels.HasValue;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MinorBodyLabels { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideSatellites { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideClouds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideOrbits { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideCometTails { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SurfacePlaces { get; init; }

    [JsonIgnore]
    internal bool HasBodyLayerSettings => HideOrbits || HideCometTails || SurfacePlaces;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CinematicBlackHoles { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EclipticLine { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConstellationBoundaries { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EclipticGrid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GalacticGrid { get; init; }
    [JsonIgnore]
    public bool HasAlternateGrids => EclipticLine || EclipticGrid || GalacticGrid;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Meteors { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MeteorRadiants { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MeteorRateMultiplier { get; init; }
    [JsonIgnore]
    public bool ShowMeteorRadiants => MeteorRadiants ?? Meteors;
    [JsonIgnore]
    public bool HasMeteorSettings => MeteorRadiants.HasValue || MeteorRateMultiplier.HasValue;

    internal static bool ValidMeteorRate(int rate) => rate is 1 or 10 or 100;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CultureId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SkyCultureNames CultureNames { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ConstellationArt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SurveyId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SurveyBrightness { get; init; }

    [JsonIgnore]
    public double EffectiveSurveyBrightness => SurveyBrightness ?? 1;

    [JsonIgnore]
    public bool HasSurveySettings => SurveyId is not null || SurveyBrightness is not null;

    [JsonIgnore]
    public bool HasCultureSettings => CultureId is not null || CultureNames != default || ConstellationArt;

    [JsonIgnore]
    public bool Visible => Stars || Labels || EquatorialGrid || HasAlternateGrids || ConstellationBoundaries || Constellations || DeepSky || Meteors || ShowMeteorRadiants || ConstellationArt || SurveyId is not null;

    internal bool IsValid() =>         (CultureId is null || SkyCultureLibrary.ValidId(CultureId)) && Enum.IsDefined(CultureNames) &&
                (SurveyId is null || LocalSkySurvey.ValidId(SurveyId)) && SurveyBrightness is null or (>= 0 and <= 2) &&
        (MeteorRateMultiplier is not { } rate || ValidMeteorRate(rate)) && (DeepSkyFilter is not { } filter || filter.IsValid());
}
