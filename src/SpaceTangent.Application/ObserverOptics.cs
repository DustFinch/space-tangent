// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public sealed record ObserverOptics(VisualOptics Profile, bool Active)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpticalFinders? Finders { get; init; }
    public bool IsValid() => Profile?.IsValid() == true && (Finders is not { } finders || finders.IsValid());
}

public sealed class ObserverOpticsLibrary
{
    private static readonly OpticalTube[] BuiltInTubes =
    [
        new("Reflector 200/1200", 200, 1200), new("Refractor 80/480", 80, 480),
        new("Schmidt-Cassegrain 235/2350", 235, 2350)
    ];
    private static readonly OpticalEyepiece[] BuiltInEyepieces =
    [
        new("Plössl 25 mm", 25, 52), new("Wide angle 15 mm", 15, 68),
        new("Wide angle 10 mm", 10, 68), new("Ultra-wide 6 mm", 6, 82)
    ];

    public static VisualOptics Default { get; } = new(BuiltInTubes[0], BuiltInEyepieces[0]);
    public IReadOnlyList<OpticalTube> Tubes { get; }
    public IReadOnlyList<OpticalEyepiece> Eyepieces { get; }

    private ObserverOpticsLibrary(OpticalTube[] tubes, OpticalEyepiece[] eyepieces)
    {
        Tubes = Array.AsReadOnly(tubes);
        Eyepieces = Array.AsReadOnly(eyepieces);
    }

    internal static ObserverOpticsLibrary LoadArchived(string path)
    {
        try { return Load(path, preserveIndices: true); }
        catch (JsonException) { return new(BuiltInTubes, BuiltInEyepieces); }
    }

    public static ObserverOpticsLibrary Load(string path) => Load(path, preserveIndices: false);

    private static ObserverOpticsLibrary Load(string path, bool preserveIndices)
    {
        if (!File.Exists(path))
            return new(BuiltInTubes, BuiltInEyepieces);
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024)
            throw new InvalidDataException("The optics profile exceeds its size limit.");
        var settings = JsonSerializer.Deserialize<UserOptics>(stream);
        var tubes = (settings?.Telescopes ?? []).OfType<OpticalTube>().Where(item => item.IsValid());
        var eyepieces = (settings?.Eyepieces ?? []).OfType<OpticalEyepiece>().Where(item => item.IsValid());

        // The archived settings store indices into these lists, including duplicate entries.
        return new([.. BuiltInTubes, .. (preserveIndices ? tubes : tubes.Distinct())],
            [.. BuiltInEyepieces, .. (preserveIndices ? eyepieces : eyepieces.Distinct())]);
    }

    public OpticalTube Next(OpticalTube tube) => Next(Tubes, tube);
    public OpticalEyepiece Next(OpticalEyepiece eyepiece) => Next(Eyepieces, eyepiece);

    private static T Next<T>(IReadOnlyList<T> entries, T current)
    {
        for (int i = 0; i < entries.Count; i++)
            if (EqualityComparer<T>.Default.Equals(entries[i], current))
                return entries[(i + 1) % entries.Count];
        return entries[0];
    }

    private sealed record UserOptics(OpticalTube?[]? Telescopes, OpticalEyepiece?[]? Eyepieces);
}
