// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Simulation;

internal static class LocalDataPaths
{
    internal static string Find(string environmentVariable, string relativePath, bool directory = false)
    {
        string? configured = System.Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string local = ProjectSettings.GlobalizePath($"res://.local-data/{relativePath}");
        if (Exists(local, directory))
            return local;

        string sidecar = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "data", relativePath);
        if (Exists(sidecar, directory))
            return sidecar;
        return ProjectSettings.GlobalizePath($"user://{relativePath}");
    }

    private static bool Exists(string path, bool directory) => directory ? Directory.Exists(path) : File.Exists(path);
}
