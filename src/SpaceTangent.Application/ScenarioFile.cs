// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public sealed record SimulationScenario(IReadOnlyList<ObserverView> Views, bool Synchronized, int ClockOwner,
    int ActiveView, DeltaTModel DeltaT)
{
    public ViewSplit SplitLayout { get; init; }
    public ObserverLayout? Layout { get; init; }
}

/// <summary>Persists observer settings with exact TDB epochs and stable catalog IDs.</summary>
public static class ScenarioFile
{
    private const int Version = 33;
    internal const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = ObserverLayout.MaximumViews + 8,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<CameraMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<ViewSplit>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<NavigationFrame>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<BodyReferenceMarks>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<SkyCultureNames>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<CameraSensorPreset>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<DeltaTModel>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    internal static byte[] Encode(SimulationScenario scenario)
    {
        ObserverLayout.Restore(scenario.Layout, scenario.Views.Count, scenario.SplitLayout);
        if (scenario.Views.Any(view => view.Epoch.Scale != TimeScale.Tdb))
            throw new InvalidDataException("A scenario needs TDB views.");
        if (scenario.Views.Any(view => view.Navigation is { } pose && !pose.IsValid()))
            throw new InvalidDataException("The scenario has an invalid camera pose.");
        if (scenario.Views.Any(view => view.EarthObserver is { } earth && !earth.IsValid()))
            throw new InvalidDataException("The scenario has an invalid Earth observer camera.");
        if (!Enum.IsDefined(scenario.SplitLayout) ||
            scenario.Views.Any(view => (view.FieldOfViewDegrees is { } field && !ObserverView.ValidFieldOfView(field)) ||
                (view.PlaybackRate is { } rate && !ObserverView.ValidPlaybackRate(rate))))
            throw new InvalidDataException("The scenario has an invalid layout or field of view.");
        if (scenario.Views.Any(view => (view.ReferenceMarks & ~BodyReferenceMarks.All) != 0))
            throw new InvalidDataException("The scenario has unsupported reference marks.");
        if (scenario.Views.Any(view => view.Optics is { } optical && !optical.IsValid()))
            throw new InvalidDataException("The scenario has an invalid optics profile.");
        if (scenario.Views.Any(view => !view.Sky.IsValid()))
            throw new InvalidDataException("The scenario has invalid sky layer settings.");
        if (scenario.Views.Any(view => view.AutoOrbit is { } orbit && !orbit.IsValid()))
            throw new InvalidDataException("The scenario has an invalid camera orbit period.");
        if (scenario.Views.Any(view => view.MouseSensitivity is { } sensitivity && !ObserverView.ValidMouseSensitivity(sensitivity)))
            throw new InvalidDataException("The scenario has an invalid camera sensitivity.");
        var document = new Document
        {
            Version = scenario.Views.Any(view => view.Sky.HideAtmospheres) ? 33 :
                scenario.Views.Any(view => view.Sky.HideClouds) ? 32 :
                scenario.Views.Any(view => view.Sky.HasDeepSkyPreferences) ? 31 :
                scenario.Views.Any(view => view.Sky.CinematicBlackHoles) ? 30 :
                scenario.Views.Any(view => view.Sky.HasBodyLayerSettings) ? 29 :
                scenario.Views.Any(view => view.Sky.HideSatellites) ? 28 :
                scenario.Views.Any(view => view.Sky.MinorBodyLabels) ? 27 :
                scenario.Views.Any(view => view.Sky.HasMeteorSettings) ? 26 :
                scenario.Views.Any(view => view.Sky.ConstellationBoundaries) ? 25 :
                scenario.Views.Any(view => view.Sky.HasAlternateGrids) ? 24 :
                scenario.Views.Any(view => view.EarthObserver?.HideLandscapeFog == true) ? 23 :
                scenario.Views.Any(view => view.CameraInertia) ? 22 :
                scenario.Views.Any(view => view.MouseSensitivity is not null) ? 21 :
                scenario.Views.Any(view => view.AutoOrbit is not null) ? 20 :
                scenario.Views.Any(view => view.Navigation?.Placement?.CatalogueFrame is not null) ? 19 :
                scenario.Views.Any(view => view.Optics?.Finders is not null) ? 18 :
                scenario.Views.Any(view => view.Navigation?.Placement?.Frame == NavigationFrame.Equatorial) ? 17 :
                scenario.Views.Any(view => view.Sky.HasSurveySettings) ? 16 :
                scenario.Views.Any(view => view.Sky.HasCultureSettings) ? 15 :
                scenario.Views.Any(view => view.Optics is not null) ? 14 :
                scenario.Views.Any(view => view.EarthObserver?.LandscapeId is not null) ? 13 :
                scenario.Views.Any(view => view.Sky.Meteors) ? 12 :
                scenario.Views.Any(view => view.EarthObserver?.Conditions is not null) ? 11 :
                scenario.Views.Any(view => view.LightTimeDelayed) ? 10 :
                scenario.Views.Any(view => view.ReferenceMarks != BodyReferenceMarks.None) ? 9 :
                scenario.Layout is not null || scenario.Views.Any(view => view.Navigation?.Placement?.Reference is not null) ? 8 : 7,
            Synchronized = scenario.Synchronized,
            ClockOwner = scenario.ClockOwner,
            ActiveView = scenario.ActiveView,
            SplitLayout = scenario.SplitLayout,
            Layout = scenario.Layout,
            DeltaT = scenario.DeltaT,
            Views = scenario.Views.Select(view => new View
            {
                DayTdb = view.Epoch.Day,
                NanosecondsOfDay = view.Epoch.NanosecondsOfDay,
                Camera = view.Mode,
                Selection = view.Selection?.Value,
                Playing = view.Playing,
                Terrain = view.Terrain,
                FieldOfViewDegrees = view.FieldOfViewDegrees,
                AutoOrbit = view.AutoOrbit,
                MouseSensitivity = view.MouseSensitivity,
                CameraInertia = view.CameraInertia,
                PlaybackRate = view.PlaybackRate,
                Navigation = view.Navigation is { } pose ? Camera.From(pose) : null,
                EarthObserver = view.EarthObserver,
                Sky = view.Sky.Visible || view.Sky.HasCultureSettings || view.Sky.HasSurveySettings || view.Sky.HasMeteorSettings || view.Sky.MinorBodyLabels || view.Sky.HideSatellites || view.Sky.HasBodyLayerSettings || view.Sky.CinematicBlackHoles || view.Sky.HasDeepSkyPreferences || view.Sky.HideClouds || view.Sky.HideAtmospheres ? view.Sky : null,
                ReferenceMarks = view.ReferenceMarks,
                Optics = view.Optics,
                LightTimeDelayed = view.LightTimeDelayed
            }).ToArray()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The scenario is too large.");
        return bytes;
    }

    public static void Save(string path, SimulationScenario scenario)
    {
        byte[] bytes = Encode(scenario);
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".scene-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }

    public static SimulationScenario Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The scenario file has an invalid size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The scenario changed while it was being read.");
        return Decode(bytes);
    }

    internal static SimulationScenario Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The scenario has an invalid size.");
        Document document = JsonSerializer.Deserialize<Document>(bytes, Json)
            ?? throw new InvalidDataException("The scenario is empty.");
        if (document.Version is < 1 or > Version)
            throw new InvalidDataException($"Unsupported scenario version {document.Version}.");
        if (!Enum.IsDefined(document.SplitLayout) || (document.Version < 6 && document.SplitLayout != ViewSplit.SideBySide))
            throw new InvalidDataException("The scenario has an invalid layout.");
        if (document.Layout is not null && document.Version < 8)
            throw new InvalidDataException("A nested layout requires a version 8 scenario.");
        if (document.Views is null || document.Views.Any(view => view is null))
            throw new InvalidDataException("The scenario has missing views.");
        ObserverLayout layout = ObserverLayout.Restore(document.Layout, document.Views.Length, document.SplitLayout);

        var views = new ObserverView[document.Views.Length];
        for (int i = 0; i < views.Length; i++)
        {
            View data = document.Views[i];
            var epoch = new JulianInstant(data.DayTdb, data.NanosecondsOfDay, TimeScale.Tdb);
            ObjectId? selection = data.Selection is { } id ? new ObjectId(id) : null;
            if (document.Version == 1 && data.Navigation is not null)
                throw new InvalidDataException("Camera poses require a version 2 scenario.");
            NavigationCamera? navigation = data.Navigation?.ToPose();
            if (navigation is { } pose && !pose.IsValid())
                throw new InvalidDataException("The scenario has an invalid camera pose.");
            if (data.EarthObserver is { } earth &&
                (document.Version < 3 || !earth.IsValid() || (document.Version < 11 && earth.Conditions is not null) ||
                    (document.Version < 13 && earth.LandscapeId is not null) || (document.Version < 23 && earth.HideLandscapeFog)))
                throw new InvalidDataException("The scenario has an invalid Earth observer camera.");
            if (data.Sky is { HideAtmospheres: true } && document.Version < 33)
                throw new InvalidDataException("Atmosphere visibility requires scenario version 33.");
            if (data.Sky is { HideClouds: true } && document.Version < 32)
                throw new InvalidDataException("Cloud visibility requires scenario version 32.");
            if (data.Sky is { HasDeepSkyPreferences: true } && document.Version < 31)
                throw new InvalidDataException("Deep-sky preferences require scenario version 31.");
            if (data.Sky is { CinematicBlackHoles: true } && document.Version < 30)
                throw new InvalidDataException("Cinematic black holes require scenario version 30.");
            if (data.Sky is { HasBodyLayerSettings: true } && document.Version < 29)
                throw new InvalidDataException("Body layer preferences require scenario version 29.");
            if (data.Sky is { HideSatellites: true } && document.Version < 28)
                throw new InvalidDataException("Satellite visibility requires scenario version 28.");
            if (data.Sky is { MinorBodyLabels: true } && document.Version < 27)
                throw new InvalidDataException("Minor body labels require scenario version 27.");
            if (data.Sky is { HasMeteorSettings: true } && document.Version < 26)
                throw new InvalidDataException("Independent meteor settings require scenario version 26.");
            if (data.Sky is { Meteors: true } && document.Version < 12)
                throw new InvalidDataException("This scenario version does not support meteor layers.");
            if (data.Sky is { } sky && (!sky.IsValid() || sky.HasCultureSettings && document.Version < 15 || sky.HasSurveySettings && document.Version < 16))
                throw new InvalidDataException("The scene sky culture settings are invalid or unsupported.");
            if (data.Sky is { ConstellationBoundaries: true } && document.Version < 25)
                throw new InvalidDataException("Constellation boundaries require scenario version 25.");
            if (data.Sky is { HasAlternateGrids: true } && document.Version < 24)
                throw new InvalidDataException("Ecliptic and galactic layers require scenario version 24.");
            if (data.Sky is not null && document.Version < 4)
                throw new InvalidDataException("Sky layers require a version 4 scenario.");
            if (data.Sky is { DeepSky: true } && document.Version < 5)
                throw new InvalidDataException("The deep-sky layer requires a version 5 scenario.");
            if ((data.FieldOfViewDegrees is { } field && !ObserverView.ValidFieldOfView(field)) ||
                (document.Version < 6 && (data.FieldOfViewDegrees.HasValue || navigation?.Placement is not null)))
                throw new InvalidDataException("The scene field or observer frame is unsupported.");
            if (document.Version < 19 && navigation?.Placement?.CatalogueFrame is not null)
                throw new InvalidDataException("Catalogue frames require scenario version 19.");
            if (document.Version < 17 && navigation?.Placement?.Frame == NavigationFrame.Equatorial)
                throw new InvalidDataException("Mean-equator observer frames require a version 17 scenario.");
            if (document.Version < 8 && navigation?.Placement?.Reference is not null)
                throw new InvalidDataException("Moving observer frames require a version 8 scenario.");
            if (data.PlaybackRate is { } rate && (document.Version < 7 || !ObserverView.ValidPlaybackRate(rate)))
                throw new InvalidDataException("The scene playback rate is unsupported.");
            if ((data.ReferenceMarks & ~BodyReferenceMarks.All) != 0 ||
                (document.Version < 9 && data.ReferenceMarks != BodyReferenceMarks.None))
                throw new InvalidDataException("The scene reference marks are unsupported.");
            if (data.Optics is { } optical && (document.Version < 14 || !optical.IsValid() ||
                document.Version < 18 && optical.Finders is not null))
                throw new InvalidDataException("The scenario has an invalid optics profile.");
            if (document.Version < 10 && data.LightTimeDelayed)
                throw new InvalidDataException("Light-time correction requires a version 10 scenario.");
            if (data.AutoOrbit is { } orbit && (document.Version < 20 || !orbit.IsValid()))
                throw new InvalidDataException("The scenario has an invalid or unsupported camera orbit period.");
            if (data.MouseSensitivity is { } sensitivity && (document.Version < 21 || !ObserverView.ValidMouseSensitivity(sensitivity)))
                throw new InvalidDataException("The scenario has an invalid or unsupported camera sensitivity.");
            if (data.CameraInertia && document.Version < 22)
                throw new InvalidDataException("Camera inertia requires scenario version 22.");
            views[i] = new(epoch, data.Camera, selection, data.Playing)
            {
                Terrain = data.Terrain,
                FieldOfViewDegrees = data.FieldOfViewDegrees,
                AutoOrbit = data.AutoOrbit,
                MouseSensitivity = data.MouseSensitivity,
                CameraInertia = data.CameraInertia,
                PlaybackRate = data.PlaybackRate,
                Navigation = navigation,
                EarthObserver = data.EarthObserver,
                Sky = data.Sky ?? default,
                ReferenceMarks = data.ReferenceMarks,
                Optics = data.Optics,
                LightTimeDelayed = data.LightTimeDelayed
            };
        }
        return new(Array.AsReadOnly(views), document.Synchronized, document.ClockOwner, document.ActiveView, document.DeltaT)
            { SplitLayout = document.SplitLayout, Layout = layout.IsDefault ? null : layout };
    }

    private sealed class Document
    {
        public required int Version { get; init; }
        public required bool Synchronized { get; init; }
        public required int ClockOwner { get; init; }
        public required int ActiveView { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ViewSplit SplitLayout { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ObserverLayout? Layout { get; init; }
        public required DeltaTModel DeltaT { get; init; }
        public required View[] Views { get; init; }
    }

    private sealed class View
    {
        public required long DayTdb { get; init; }
        public required long NanosecondsOfDay { get; init; }
        public required CameraMode Camera { get; init; }
        public required string? Selection { get; init; }
        public required bool Playing { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LunarTerrainCamera? Terrain { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? FieldOfViewDegrees { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CameraAutoOrbit? AutoOrbit { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? MouseSensitivity { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool CameraInertia { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? PlaybackRate { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Camera? Navigation { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public EarthObserverCamera? EarthObserver { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SkyLayerSettings? Sky { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public BodyReferenceMarks ReferenceMarks { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ObserverOptics? Optics { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LightTimeDelayed { get; init; }
    }

    private sealed record Camera(double AnchorX, double AnchorY, double AnchorZ, double RangeKilometers,
        double AzimuthRadians, double ElevationRadians, string? Target, bool Tracking)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Placement? ObserverPlacement { get; init; }

        internal static Camera From(NavigationCamera pose) => new(pose.AnchorKilometers.X, pose.AnchorKilometers.Y,
            pose.AnchorKilometers.Z, pose.RangeKilometers, pose.AzimuthRadians, pose.ElevationRadians,
            pose.Target?.Value, pose.Tracking)
        {
            ObserverPlacement = pose.Placement is { } placement ? Placement.From(placement) : null
        };

        internal NavigationCamera ToPose() => new(new(AnchorX, AnchorY, AnchorZ), RangeKilometers,
            AzimuthRadians, ElevationRadians, Target is { } id ? new ObjectId(id) : null, Tracking)
        {
            Placement = ObserverPlacement?.ToPlacement()
        };
    }

    private sealed record Placement(Components Offset, Components Forward, Components Up, bool BodyFixed)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public NavigationFrame Frame { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reference { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CatalogueFrameData? CatalogueFrame { get; init; }

        internal static Placement From(NavigationPlacement pose) =>
            new(Components.From(pose.OffsetKilometers), Components.From(pose.Forward), Components.From(pose.Up), pose.BodyFixed)
            {
                Frame = pose.Frame, Reference = pose.Reference?.Value,
                CatalogueFrame = pose.CatalogueFrame is { } frame
                    ? new(frame.SelectionEpoch.Day, frame.SelectionEpoch.NanosecondsOfDay, frame.Orbit) : null
            };

        internal NavigationPlacement ToPlacement() => new(Offset.ToVector(), Forward.ToVector(), Up.ToVector(), BodyFixed)
            {
                Frame = Frame, Reference = Reference is { } id ? new ObjectId(id) : null,
                CatalogueFrame = CatalogueFrame is { } frame
                    ? new(new(frame.Day, frame.NanosecondsOfDay, TimeScale.Tdb), frame.Orbit) : null
            };
    }

    private sealed record CatalogueFrameData(long Day, long NanosecondsOfDay, bool Orbit);

    private readonly record struct Components(double X, double Y, double Z)
    {
        internal static Components From(Vector3d vector) => new(vector.X, vector.Y, vector.Z);
        internal Vector3d ToVector() => new(X, Y, Z);
    }
}
