// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal readonly record struct LocalDataPath(string? Explicit, string Project, string Export, string User, bool Directory)
{
    internal string Resolve()
    {
        if (Explicit is not null)
            return Explicit;
        if (Exists(Project))
            return Project;
        return Exists(Export) ? Export : User;
    }

    internal LocalDataPath WithExtension(string extension) =>
        new(Explicit, Path.ChangeExtension(Project, extension), Path.ChangeExtension(Export, extension),
            Path.ChangeExtension(User, extension), Directory);

    private bool Exists(string path) => Directory ? AddonDependencies.DirectoryExists(path) : AddonDependencies.FileExists(path);
}

internal static class LocalDataPaths
{
    internal static string Find(string environmentVariable, string relativePath, bool directory = false)
        => Candidates(environmentVariable, relativePath, directory).Resolve();

    internal static LocalDataPath Candidates(string environmentVariable, string relativePath, bool directory = false)
    {
        string? configured = System.Environment.GetEnvironmentVariable(environmentVariable);
        string local = ProjectSettings.GlobalizePath($"res://.local-data/{relativePath}");
        string sidecar = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "data", relativePath);
        return new(string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured), local, sidecar,
            ProjectSettings.GlobalizePath($"user://{relativePath}"), directory);
    }
}
