// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record SimulationScenario(IReadOnlyList<ObserverView> Views, bool Synchronized, int ClockOwner,
    int ActiveView, DeltaTModel DeltaT);

/// <summary>Persists observer settings with exact TDB epochs and stable catalog IDs.</summary>
public static class ScenarioFile
{
    private const int Version = 2;
    private const int MaximumBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter<CameraMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<DeltaTModel>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
    };

    public static void Save(string path, SimulationScenario scenario)
    {
        if (scenario.Views.Count is < 1 or > 2 || scenario.Views.Any(view => view.Epoch.Scale != TimeScale.Tdb))
            throw new InvalidDataException("A scenario needs one or two TDB views.");
        if (scenario.Views.Any(view => view.Navigation is { } pose && !pose.IsValid()))
            throw new InvalidDataException("The scenario has an invalid camera pose.");
        var document = new Document
        {
            Version = Version,
            Synchronized = scenario.Synchronized,
            ClockOwner = scenario.ClockOwner,
            ActiveView = scenario.ActiveView,
            DeltaT = scenario.DeltaT,
            Views = scenario.Views.Select(view => new View
            {
                DayTdb = view.Epoch.Day,
                NanosecondsOfDay = view.Epoch.NanosecondsOfDay,
                Camera = view.Mode,
                Selection = view.Selection?.Value,
                Playing = view.Playing,
                Terrain = view.Terrain,
                Navigation = view.Navigation is { } pose ? Camera.From(pose) : null
            }).ToArray()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The scenario is too large.");

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
        Document document = JsonSerializer.Deserialize<Document>(bytes, Json)
            ?? throw new InvalidDataException("The scenario is empty.");
        if (document.Version is not (1 or Version))
            throw new InvalidDataException($"Unsupported scenario version {document.Version}.");
        if (document.Views is not { Length: 1 or 2 } || document.Views.Any(view => view is null))
            throw new InvalidDataException("A scenario must have one or two views.");

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
            views[i] = new(epoch, data.Camera, selection, data.Playing) { Terrain = data.Terrain, Navigation = navigation };
        }
        return new(Array.AsReadOnly(views), document.Synchronized, document.ClockOwner, document.ActiveView, document.DeltaT);
    }

    private sealed class Document
    {
        public required int Version { get; init; }
        public required bool Synchronized { get; init; }
        public required int ClockOwner { get; init; }
        public required int ActiveView { get; init; }
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
        public Camera? Navigation { get; init; }
    }

    private sealed record Camera(double AnchorX, double AnchorY, double AnchorZ, double RangeKilometers,
        double AzimuthRadians, double ElevationRadians, string? Target, bool Tracking)
    {
        internal static Camera From(NavigationCamera pose) => new(pose.AnchorKilometers.X, pose.AnchorKilometers.Y,
            pose.AnchorKilometers.Z, pose.RangeKilometers, pose.AzimuthRadians, pose.ElevationRadians,
            pose.Target?.Value, pose.Tracking);

        internal NavigationCamera ToPose() => new(new(AnchorX, AnchorY, AnchorZ), RangeKilometers,
            AzimuthRadians, ElevationRadians, Target is { } id ? new ObjectId(id) : null, Tracking);
    }
}
