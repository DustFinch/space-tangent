// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Addons;

public readonly record struct AddonSourcePath(string? Explicit, string Project, string Export, string User, bool Directory)
{
    public string Resolve()
    {
        if (Explicit is not null)
            return Explicit;
        if (Exists(Project))
            return Project;
        return Exists(Export) ? Export : User;
    }

    public AddonSourcePath WithExtension(string extension) =>
        new(Explicit, Path.ChangeExtension(Project, extension), Path.ChangeExtension(Export, extension),
            Path.ChangeExtension(User, extension), Directory);

    private bool Exists(string path) => Directory ? AddonDependencies.DirectoryExists(path) : AddonDependencies.FileExists(path);
}
