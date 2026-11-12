// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpaceTangent.Application;

public sealed record ObserverPluginScript(string File, string Title, string? Description);

public sealed record ObserverPluginManifest(string Root, string Id, string Name, string Version,
    string? Main, IReadOnlyList<ObserverPluginScript> Scripts)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ObserverPluginManifest Load(string file)
    {
        string path = Path.GetFullPath(file);
        string root = Path.GetDirectoryName(path)!;
        if (!Path.GetFileName(path).Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Open a plugin.json manifest.");
        string json = ReadFile(root, Path.GetFileName(path), 64 * 1024);
        var metadata = JsonSerializer.Deserialize<Document>(json, Json);
        if (metadata is not { Format: 1, Api: 1 } || !ValidId(metadata.Id) || !Label(metadata.Name) || !Label(metadata.Version) ||
            (metadata.Scripts?.Length ?? 0) > 64)
            throw new InvalidDataException("The plugin manifest has an unsupported format, ID or metadata.");
        var scripts = metadata.Scripts ?? [];
        if (metadata.Main is { } main)
            _ = Resolve(root, main, lua: true);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObserverPluginScript entry in scripts)
        {
            if (entry is null || !Label(entry.Title) || !files.Add(entry.File))
                throw new InvalidDataException("The plugin manifest has invalid or duplicate scripts.");
            _ = Resolve(root, entry.File, lua: true);
        }
        return new(root, metadata.Id!, metadata.Name!, metadata.Version!, metadata.Main, Array.AsReadOnly(scripts));
    }

    internal string ReadLua(string name) => ReadFile(Root, name, 32 * 1024, lua: true);

    private static string ReadFile(string root, string name, int maximum, bool lua = false)
    {
        using var stream = File.OpenRead(Resolve(root, name, lua));
        if (stream.Length > maximum)
            throw new InvalidDataException("Plugin resource exceeds its size limit.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            content = content[3..];
        return StrictUtf8.GetString(content);
    }

    private static string Resolve(string root, string relative, bool lua)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || Path.IsPathRooted(relative) ||
            relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
            lua && !Path.GetExtension(relative).Equals(".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Plugin resources must be local Lua paths.");
        string path = Path.GetFullPath(relative, root);
        for (string cursor = path; ; cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Plugin resources cannot be symbolic links.");
            if (Path.GetRelativePath(root, cursor) == ".")
                return path;
        }
    }

    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 64 } &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160 &&
        !value.Any(char.IsControl);

    private sealed record Document(int Format, int Api, string? Id, string? Name, string? Version,
        string? Main, ObserverPluginScript[]? Scripts);
}
