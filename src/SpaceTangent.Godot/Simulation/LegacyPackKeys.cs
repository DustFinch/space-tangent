// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static class LegacyPackKeys
{
    internal static string[] For(LocalDataPath location)
    {
        string path = location.Resolve();
        string root = location.Directory ? path : Path.GetDirectoryName(path)!;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));

        string canonical = OperatingSystem.IsWindows() ? root.Replace('\\', '/').ToUpperInvariant() : root;
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\0"))).ToLowerInvariant();
        string manifest = Path.Combine(root, "pack.json");
        string? id = ManifestId(manifest);
        return id is null
            ? [name, "addon-root:" + fingerprint]
            : [id, name, "addon-root:" + fingerprint];
    }

    private static string? ManifestId(string path)
    {
        if (!AddonDependencies.FileExists(path))
            return null;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 64 * 1024 * 1024)
                return null;
            using var document = JsonDocument.Parse(file, new() { MaxDepth = 64 });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException)
        {
            // The normal loader reports errors in the selected pack.
            return null;
        }
    }
}
