// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Application.Assets;
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
            HideLandscapeFog = Flag(root, "LandscapeFog") is { } showFog ? !showFog : camera.HideLandscapeFog,
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
                ConstellationBoundaries = (layers & 4) != 0,
                EclipticLine = (layers & 8) != 0,
                EclipticGrid = (layers & 16) != 0,
                GalacticGrid = (layers & 32) != 0,
                DeepSky = (layers & 64) != 0,
                ConstellationArt = (layers & 128) != 0
            };
        if (Word(root, "SkyCulture") is { } culture && SkyCultureLibrary.ValidId(culture))
            sky = sky with { CultureId = culture };
        if (Enum.TryParse(Word(root, "NameStyle"), out SkyCultureNames names) && Enum.IsDefined(names))
            sky = sky with { CultureNames = names };
        if (Word(root, "HipsSurvey") is { } survey && (survey.Length == 0 || LocalSkySurvey.ValidId(survey)))
            sky = sky with { SurveyId = survey.Length == 0 ? null : survey };
        int? meteorRate = Integer(root, "MeteorBoost");
        sky = sky with
        {
            Labels = Flag(root, "Labels") ?? sky.Labels,
            MinorBodyLabels = Flag(root, "MinorLabels") ?? sky.MinorBodyLabels,
            HideOrbits = Flag(root, "Orbits") is { } orbits ? !orbits : sky.HideOrbits,
            HideCometTails = Flag(root, "CometTails") is { } tails ? !tails : sky.HideCometTails,
            SurfacePlaces = Flag(root, "Places") ?? sky.SurfacePlaces,
            HideSatellites = Flag(root, "Satellites") is { } satellites ? !satellites : sky.HideSatellites,
            Meteors = Flag(root, "Meteors") ?? sky.Meteors,
            MeteorRadiants = Flag(root, "Radiants") ?? sky.MeteorRadiants,
            MeteorRateMultiplier = meteorRate is { } rate && SkyLayerSettings.ValidMeteorRate(rate)
                ? (rate == 1 ? null : rate) : sky.MeteorRateMultiplier
        };
        var marks = Integer(root, "BodyMarks") is { } flags ? (BodyReferenceMarks)flags & BodyReferenceMarks.All : current.ReferenceMarks;
        double? field = Number(root, "FieldOfViewDegrees") is { } degrees && ObserverView.ValidFieldOfView(degrees)
            ? degrees : current.FieldOfViewDegrees;
        var optics = current.Optics;
        int? tube = Integer(root, "Telescope"), lens = Integer(root, "Eyepiece");
        int? barlow = Integer(root, "Barlow");
        double? seeing = Number(root, "Seeing");
        bool? inverted = Flag(root, "InvertEyepiece"), hold = Flag(root, "HoldInEyepiece");
        if (tube.HasValue || lens.HasValue || barlow is >= 0 and <= 2 || seeing is >= 0 and <= 10 ||
            inverted.HasValue || hold.HasValue)
        {
            var profile = optics?.Profile ?? ObserverOpticsLibrary.Default;
            if (tube.HasValue || lens.HasValue)
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
                var library = ObserverOpticsLibrary.LoadArchived(Path.Combine(directory, "optics-v1.json"));
                profile = profile with
                {
                    Tube = tube is >= 0 && tube < library.Tubes.Count ? library.Tubes[tube.Value] : profile.Tube,
                    Eyepiece = lens is >= 0 && lens < library.Eyepieces.Count ? library.Eyepieces[lens.Value] : profile.Eyepiece
                };
            }
            profile = profile with
            {
                Barlow = barlow is >= 0 and <= 2 ? barlow.Value + 1 : profile.Barlow,
                SeeingArcseconds = seeing is >= 0 and <= 10 ? seeing.Value : profile.SeeingArcseconds,
                Inverted = inverted ?? profile.Inverted,
                HoldSelection = hold ?? profile.HoldSelection
            };
            if (profile.IsValid())
                optics = optics is null ? new(profile, false) : optics with { Profile = profile };
        }
        bool? telrad = Flag(root, "Telrad"), circle = Flag(root, "FinderCircle");
        int? sensor = Integer(root, "Camera");
        if (telrad.HasValue || circle.HasValue || sensor.HasValue)
        {
            var previous = optics?.Finders ?? default;
            var preset = sensor is >= 0 and <= 4 ? (CameraSensorPreset)sensor.Value : previous.Sensor;
            var finders = new OpticalFinders(telrad ?? previous.Telrad, preset, circle ?? previous.EyepieceCircle);
            if (optics is not null || finders.Visible)
                optics = (optics ?? new(ObserverOpticsLibrary.Default, false)) with { Finders = finders.Visible ? finders : null };
        }
        var autoOrbit = current.AutoOrbit;
        double? period = Number(root, "AutoRotateSeconds");
        bool? clockwise = Flag(root, "AutoRotateClockwise");
        if (period is >= 20 and <= 1200 || clockwise.HasValue)
        {
            var previous = autoOrbit ?? CameraAutoOrbit.Default;
            autoOrbit = previous with
            {
                PeriodSeconds = period is >= 20 and <= 1200 ? period.Value : previous.PeriodSeconds,
                Clockwise = clockwise ?? previous.Clockwise
            };
        }
        double? sensitivity = Number(root, "MouseSensitivity");
        var observer = current with
        {
            EarthObserver = camera.IsValid() ? camera : current.EarthObserver,
            Sky = sky, ReferenceMarks = marks, FieldOfViewDegrees = field, Optics = optics, AutoOrbit = autoOrbit,
            CameraInertia = Flag(root, "Inertia") ?? current.CameraInertia,
            MouseSensitivity = sensitivity is { } value && ObserverView.ValidMouseSensitivity(value)
                ? (value == 1 ? null : value) : current.MouseSensitivity
        };
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
