// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record LegacyObserverProfile(ObserverView Observer, DeltaTModel DeltaT)
{
    public IReadOnlyList<EarthPlace> SavedSites { get; init; } = [];
}

public static class LegacyObserverProfiles
{
    /// <summary>Applies archived functional settings to an existing observer.</summary>
    public static LegacyObserverProfile Read(string file, ObserverView current, DeltaTModel deltaT)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > 256 * 1024)
            throw new InvalidDataException("The archived observer profile exceeds its size limit.");
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Site", out _) && !root.TryGetProperty("DeltaT", out _))
            throw new InvalidDataException("Expected an archived observer settings document.");

        if (Enum.TryParse(Word(root, "DeltaT"), out DeltaTModel model) && Enum.IsDefined(model))
            deltaT = model;
        EarthObserverCamera camera = current.EarthObserver ?? EarthObserverCamera.Moscow;
        if (Word(root, "SiteBody") is null or "Sol/Earth" && root.TryGetProperty("Site", out var position) &&
            position.ValueKind == JsonValueKind.Object)
        {
            var site = new EarthSite(Number(position, "LatitudeDegrees") ?? camera.Site.LatitudeDegrees,
                Number(position, "LongitudeDegrees") ?? camera.Site.LongitudeDegrees,
                Math.Max(0, (Number(position, "AltitudeMeters") ?? 1000 * camera.Site.HeightKilometers) / 1000));
            if (site.IsValid())
                camera = camera with { Site = site };
        }
        var conditions = camera.Conditions ?? new GroundSkyConditions(4);
        conditions = conditions with
        {
            Bortle = Integer(root, "Bortle") is >= 1 and <= 9 and var bortle ? bortle : conditions.Bortle,
            TemperatureCelsius = Number(root, "AirTemperature") is >= -100 and <= 60 and var temperature
                ? temperature : conditions.TemperatureCelsius,
            PressureHectopascals = root.TryGetProperty("AirPressure", out var barometer) && barometer.ValueKind == JsonValueKind.Null
                ? null : Number(root, "AirPressure") is >= 0 and <= 1100 and var pressure ? pressure : conditions.PressureHectopascals,
            Refraction = Flag(root, "Refraction") ?? conditions.Refraction,
            Scintillation = Flag(root, "Twinkle") ?? conditions.Scintillation
        };
        camera = camera with
        {
            Conditions = conditions,
            CardinalPoints = Flag(root, "Cardinals") ?? camera.CardinalPoints,
            HorizonGrid = Flag(root, "HorizonGrid") ?? camera.HorizonGrid,
            LandscapeId = Word(root, "Landscape") switch
            {
                "" => null,
                { } landscape when ObservingLandscape.ValidId(landscape) => landscape,
                _ => camera.LandscapeId
            }
        };
        var sky = current.Sky;
        if (Integer(root, "SkyLayers") is { } layers)
            sky = sky with
            {
                Constellations = (layers & 1) != 0,
                EquatorialGrid = (layers & 2) != 0,
                DeepSky = (layers & 64) != 0,
                ConstellationArt = (layers & 128) != 0
            };
        if (Word(root, "SkyCulture") is { } culture && SkyCultureLibrary.ValidId(culture))
            sky = sky with { CultureId = culture };
        if (Enum.TryParse(Word(root, "NameStyle"), out SkyCultureNames names) && Enum.IsDefined(names))
            sky = sky with { CultureNames = names };
        sky = sky with { Labels = Flag(root, "Labels") ?? sky.Labels, Meteors = Flag(root, "Meteors") ?? sky.Meteors };
        var marks = Integer(root, "BodyMarks") is { } flags ? (BodyReferenceMarks)flags & BodyReferenceMarks.All : current.ReferenceMarks;
        double? field = Number(root, "FieldOfViewDegrees") is { } degrees && ObserverView.ValidFieldOfView(degrees)
            ? degrees : current.FieldOfViewDegrees;
        var optics = current.Optics;
        if (Integer(root, "Telescope") is { } tube && Integer(root, "Eyepiece") is { } lens)
        {
            var library = ObserverOpticsLibrary.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "optics-v1.json"));
            var defaults = ObserverOpticsLibrary.Default;
            var profile = defaults with
            {
                Tube = tube >= 0 && tube < library.Tubes.Count ? library.Tubes[tube] : defaults.Tube,
                Eyepiece = lens >= 0 && lens < library.Eyepieces.Count ? library.Eyepieces[lens] : defaults.Eyepiece,
                Barlow = Integer(root, "Barlow") is >= 0 and <= 2 and var multiplier ? multiplier + 1 : defaults.Barlow,
                SeeingArcseconds = Number(root, "Seeing") is >= 0 and <= 10 and var seeing ? seeing : defaults.SeeingArcseconds,
                Inverted = Flag(root, "InvertEyepiece") ?? defaults.Inverted,
                HoldSelection = Flag(root, "HoldInEyepiece") ?? defaults.HoldSelection
            };
            if (profile.IsValid())
                optics = new(profile, false);
        }
        var observer = current with { EarthObserver = camera.IsValid() ? camera : current.EarthObserver,
            Sky = sky, ReferenceMarks = marks, FieldOfViewDegrees = field, Optics = optics };
        return new(observer, deltaT) { SavedSites = EarthPlaceCatalogue.ParseSavedText(Word(root, "SavedSites")) };
    }

    private static double? Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;

    private static int? Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    private static bool? Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    } : null;

    private static string? Word(JsonElement element, string name) => element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
