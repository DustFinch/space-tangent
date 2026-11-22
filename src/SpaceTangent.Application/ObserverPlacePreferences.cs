// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

/// <summary>Automatic place preferences, independent of epochs, navigation and saved startup workspaces.</summary>
public static class ObserverPlacePreferences
{
    public const int MaximumBytes = 256 * 1024;
    private sealed record Entry([property: JsonRequired] Guid Key, [property: JsonRequired] EarthObserverCamera Camera,
        [property: JsonRequired] EarthObserverCamera[] Locations);
    private sealed record Document([property: JsonRequired] int Version, [property: JsonRequired] Entry[] Views);

    public static byte[] Capture(SimulationSession session)
    {
        var entries = Enumerable.Range(0, session.ViewCount).Select(index =>
        {
            var view = session[index];
            return new Entry(view.Places.ViewKey, view.ObserverCamera, view.Places.Locations.ToArray());
        }).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries), ScenarioFile.Json);
        Validate(bytes);
        return bytes;
    }

    public static void Save(string path, SimulationSession session)
    {
        byte[] bytes = Capture(session);
        string full = Path.GetFullPath(path);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            RemoveStaleTemporaryFiles(full);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void RemoveStaleTemporaryFiles(string target)
    {
        string prefix = Path.GetFileName(target) + ".";
        DateTime cutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(target)!))
            {
                string name = Path.GetFileName(candidate);
                if (name.Length != prefix.Length + 32 + 4 || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                    !name.EndsWith(".tmp", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(candidate) < cutoff) File.Delete(candidate);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static byte[] Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Observing place preferences are too large.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Validate(bytes);
        return bytes;
    }

    public static int Apply(ReadOnlySpan<byte> bytes, SimulationSession session, bool adoptSingleDefault = false)
    {
        Document document = Validate(bytes);
        int applied = 0;
        for (int index = 0; index < session.ViewCount; index++)
        {
            Entry? entry = document.Views.FirstOrDefault(saved => saved.Key == session[index].Places.ViewKey);
            if (entry is null && adoptSingleDefault && session.ViewCount == 1 &&
                session[0].EarthObserver is null && session[0].Places.Locations.Count == 0)
                entry = document.Views[0];
            if (entry is null) continue;
            var memory = ObserverPlaceMemory.FromSaved(entry.Key, entry.Locations);
            session.RestoreObserverPlaces(index, entry.Camera, memory);
            applied++;
        }
        return applied;
    }

    private static Document Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Observing place preferences are too large.");
        try
        {
            // Reject malformed UTF-8 rather than silently changing a user's place name.
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using (var syntax = JsonDocument.Parse(bytes.ToArray())) ScenarioFile.UniqueFields(syntax.RootElement);
            var document = JsonSerializer.Deserialize<Document>(bytes, ScenarioFile.Json);
            if (document is not { Version: 1, Views.Length: > 0 and <= ObserverLayout.MaximumViews } ||
                document.Views.Any(entry => entry is null || !entry.Camera.IsValid()) ||
                document.Views.Select(entry => entry.Key).Distinct().Count() != document.Views.Length)
                throw new InvalidDataException("Invalid observing place preferences.");
            foreach (var entry in document.Views) _ = ObserverPlaceMemory.FromSaved(entry.Key, entry.Locations);
            return document;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("Could not decode observing place preferences.", error); }
    }
}
