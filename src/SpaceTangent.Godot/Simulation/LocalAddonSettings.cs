// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace SpaceTangent.Godot.Simulation;

internal enum LocalAddonKind
{
    Science,
    Mission,
    Timeline,
    Terrain
}

internal sealed record LocalAddonSource(Guid Id, LocalAddonKind Kind, string Path, bool RelativeToData, bool Enabled);

internal sealed record LocalAddonSettings(LocalAddonKind[] DisabledDefaults, LocalAddonSource[] Sources)
{
    internal static LocalAddonSettings Empty { get; } = new([], []);

    internal bool UsesDefault(LocalAddonKind kind) => !DisabledDefaults.Contains(kind);

    internal LocalAddonSettings SetDefault(LocalAddonKind kind, bool enabled)
    {
        var defaults = DisabledDefaults.Where(item => item != kind).ToList();
        if (!enabled)
            defaults.Add(kind);
        LocalAddonSource[] sources = enabled && kind != LocalAddonKind.Science
            ? Sources.Select(source => source.Kind == kind ? source with { Enabled = false } : source).ToArray()
            : Sources;
        return new(defaults.ToArray(), sources);
    }

    internal LocalAddonSettings SetSource(LocalAddonSource selected, bool enabled)
    {
        bool firstScienceSource = selected.Kind == LocalAddonKind.Science &&
            !Sources.Any(source => source.Enabled && source.Kind == LocalAddonKind.Science);
        var current = enabled && (selected.Kind != LocalAddonKind.Science || firstScienceSource)
            ? SetDefault(selected.Kind, false)
            : this;
        var sources = current.Sources.Select(source =>
        {
            if (source.Id == selected.Id)
                return source with { Enabled = enabled };
            if (enabled && selected.Kind != LocalAddonKind.Science && source.Kind == selected.Kind)
                return source with { Enabled = false };
            return source;
        }).ToList();
        if (!sources.Any(source => source.Id == selected.Id))
            sources.Add(selected with { Enabled = enabled });
        return current with { Sources = sources.ToArray() };
    }
}

internal sealed class LocalAddonSettingsFile
{
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumSources = 64;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 6,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<LocalAddonKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _file;
    private readonly string _data;

    internal LocalAddonSettingsFile()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH");
        _file = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://local-addons.json") : configured);
        string projectData = ProjectSettings.GlobalizePath("res://.local-data");
        _data = Path.GetFullPath(Directory.Exists(projectData) ? projectData
            : Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "data"));
    }

    internal LocalAddonSettings Load()
    {
        if (!File.Exists(_file))
            return LocalAddonSettings.Empty;
        using FileStream stream = File.OpenRead(_file);
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The local add-on settings have an invalid size.");
        Document data = JsonSerializer.Deserialize<Document>(stream, Json)
            ?? throw new InvalidDataException("The local add-on settings are empty.");
        if (data.Version != 1 || data.DisabledDefaults is null || data.Sources is null)
            throw new InvalidDataException("Unsupported local add-on settings.");
        var settings = new LocalAddonSettings(data.DisabledDefaults, data.Sources);
        Validate(settings);
        return settings;
    }

    internal void Save(LocalAddonSettings settings)
    {
        Validate(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, settings.DisabledDefaults, settings.Sources), Json);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The local add-on settings are too large.");
        string directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".addons-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal LocalAddonSource FindOrCreate(LocalAddonSettings settings, LocalAddonKind kind, string path)
    {
        string absolute = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var existing = settings.Sources.FirstOrDefault(source =>
            source.Kind == kind && string.Equals(Resolve(source), absolute, PathComparison));
        if (existing is not null)
            return existing;
        string relative = Path.GetRelativePath(_data, absolute);
        bool portable = IsWithinData(relative);
        return new(Guid.NewGuid(), kind, portable ? relative.Replace(Path.DirectorySeparatorChar, '/') : absolute, portable, true);
    }

    internal string Resolve(LocalAddonSource source)
    {
        if (source.RelativeToData && !IsWithinData(source.Path))
            throw new InvalidDataException("An add-on path leaves the portable data directory.");
        if (!source.RelativeToData && !Path.IsPathFullyQualified(source.Path))
            throw new InvalidDataException("An add-on path must be absolute or relative to portable data.");
        string result = Path.GetFullPath(source.RelativeToData ? Path.Combine(_data, source.Path) : source.Path);
        if (source.RelativeToData && !IsWithinData(Path.GetRelativePath(_data, result)))
            throw new InvalidDataException("An add-on path leaves the portable data directory.");
        return result;
    }

    private static bool IsWithinData(string path) =>
        !Path.IsPathRooted(path) && path != ".." && !path.StartsWith("../", StringComparison.Ordinal) &&
        !path.StartsWith("..\\", StringComparison.Ordinal);

    private void Validate(LocalAddonSettings settings)
    {
        if (settings.Sources.Length > MaximumSources ||
            settings.Sources.Any(source => source is null || source.Id == Guid.Empty || string.IsNullOrWhiteSpace(source.Path)) ||
            settings.Sources.Select(source => source.Id).Distinct().Count() != settings.Sources.Length)
            throw new InvalidDataException("The local add-on settings contain an invalid source list.");
        foreach (LocalAddonSource source in settings.Sources)
            _ = Resolve(source);
        foreach (LocalAddonKind kind in Enum.GetValues<LocalAddonKind>())
        {
            if (kind != LocalAddonKind.Science &&
                settings.Sources.Count(source => source.Enabled && source.Kind == kind) + (settings.UsesDefault(kind) ? 1 : 0) > 1)
                throw new InvalidDataException("Only one source may be enabled for this add-on kind.");
        }
    }

    private sealed record Document(int Version, LocalAddonKind[] DisabledDefaults, LocalAddonSource[] Sources);
}
