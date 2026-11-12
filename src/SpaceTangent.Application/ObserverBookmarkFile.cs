// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public sealed record ObserverBookmark(string Name, string Link);

public sealed class ObserverBookmarkFile(string path)
{
    private const int MaximumBookmarks = 256;
    private const int MaximumBytes = 4 * 1024 * 1024;

    public IReadOnlyList<ObserverBookmark> Load()
    {
        if (!File.Exists(path))
            return [];
        using FileStream input = File.OpenRead(path);
        if (input.Length > MaximumBytes)
            throw new InvalidDataException("The bookmark file is too large.");
        var items = JsonSerializer.Deserialize<ObserverBookmark[]>(input) ??
            throw new InvalidDataException("The bookmark file is empty.");
        Validate(items);
        return Array.AsReadOnly(items);
    }

    public void Save(IReadOnlyList<ObserverBookmark> bookmarks)
    {
        Validate(bookmarks);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bookmarks);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The bookmark file is too large.");
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".bookmarks-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public (IReadOnlyList<ObserverBookmark> Bookmarks, int Added, int Skipped) ImportFavorites(string source,
        IReadOnlyList<ObserverBookmark> current, SimulationWorld world, DeltaTModel deltaT)
    {
        using FileStream input = File.OpenRead(source);
        if (input.Length > ObserverFavorites.MaximumCharacters * 4)
            throw new InvalidDataException("The favorites file is too large.");
        using var reader = new StreamReader(input);
        var favorites = ObserverFavorites.Parse(reader.ReadToEnd());
        var result = current.ToList();
        var names = new HashSet<string>(current.Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (ObserverFavorite item in favorites)
        {
            string name = (string.IsNullOrWhiteSpace(item.Folder) ? item.Name : $"{item.Folder} / {item.Name}").Trim();
            if (name.Length > 100)
                name = name[..100];
            if (result.Count == MaximumBookmarks || name.Length == 0 || name.Any(char.IsControl) || names.Contains(name) ||
                SceneLink.RestoreFavorite(item, world, deltaT) is not { } scene)
            {
                skipped++;
                continue;
            }
            result.Add(new(name, SceneLink.Write(scene)));
            names.Add(name);
        }
        if (result.Count > current.Count)
            Save(result);
        return (result.AsReadOnly(), result.Count - current.Count, skipped);
    }

    private static void Validate(IReadOnlyList<ObserverBookmark> bookmarks)
    {
        if (bookmarks.Count > MaximumBookmarks)
            throw new InvalidDataException("The bookmark list contains more than 256 items.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObserverBookmark item in bookmarks)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 ||
                item.Name.Any(char.IsControl) || !names.Add(item.Name) || item.Link is null ||
                item.Link.Length > SceneLink.MaximumLength || !item.Link.StartsWith(SceneLink.Scheme, StringComparison.Ordinal))
                throw new InvalidDataException("A bookmark has an invalid or repeated name or a malformed link.");
        }
    }
}
