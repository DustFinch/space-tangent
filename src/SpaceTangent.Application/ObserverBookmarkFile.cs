// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;

namespace SpaceTangent.Application;

public sealed record ObserverBookmark(string Name, string Link);

public sealed class ObserverBookmarkFile(string path)
{
    private const int MaximumBookmarks = 64;
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

    private static void Validate(IReadOnlyList<ObserverBookmark> bookmarks)
    {
        if (bookmarks.Count > MaximumBookmarks)
            throw new InvalidDataException("The bookmark list contains more than 64 items.");
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
