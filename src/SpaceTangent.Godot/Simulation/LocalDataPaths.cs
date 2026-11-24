// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Addons;

using SpaceTangent.Application.Addons;

namespace SpaceTangent.Godot.Simulation;

internal static class LocalDataPaths
{
    internal static string Find(string environmentVariable, string relativePath, bool directory = false)
        => Candidates(environmentVariable, relativePath, directory).Resolve();

    internal static AddonSourcePath Candidates(string environmentVariable, string relativePath, bool directory = false)
    {
        string? configured = System.Environment.GetEnvironmentVariable(environmentVariable);
        string local = ProjectSettings.GlobalizePath($"res://.local-data/{relativePath}");
        string sidecar = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "data", relativePath);
        return new(string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured), local, sidecar,
            ProjectSettings.GlobalizePath($"user://{relativePath}"), directory);
    }
}
