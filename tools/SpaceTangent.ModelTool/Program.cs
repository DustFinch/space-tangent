// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Core.Addons;

if (args is not ["model", var input, var output, .. var options] ||
    options is not ([] or ["--native-body"] or ["--root", _] or ["--root", _, "--native-body"]))
{
    Console.Error.WriteLine("Usage: model <input> <out.glb> [--root <add-on folder>] [--native-body]");
    return 2;
}

try
{
    string source = Path.GetFullPath(input);
    string target = Path.GetFullPath(output);
    string root = options is ["--root", var folder, ..] ? Path.GetFullPath(folder) : Path.GetDirectoryName(source)!;
    bool nativeBody = options is ["--native-body"] or ["--root", _, "--native-body"];
    string extension = Path.GetExtension(source).ToLowerInvariant();
    if (nativeBody && extension != ".cmod")
        throw new NotSupportedException("--native-body applies to CMOD input.");
    string relative = Path.GetRelativePath(root, source);
    if (Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("The input model must be inside its add-on root.");
    if (!Path.GetExtension(target).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The output filename must end in .glb.");
    if (Path.Exists(target))
        throw new IOException("The output already exists.");

    DecodedScienceModel model = extension switch
    {
        ".obj" => ScienceModelDecoder.Decode(source),
        ".cmod" => CmodMeshDecoder.DecodeForModelExport(root, source, nativeBody),
        ".cms" => DisplacedSphereDecoder.Decode(source),
        ".3ds" => StudioMeshDecoder.Decode(root, source),
        ".glb" or ".gltf" => PackedModelDecoder.Decode(root, source),
        _ => throw new NotSupportedException("Expected OBJ, CMOD, CMS, 3DS, glTF or GLB.")
    };
    string directory = Path.GetDirectoryName(target)!;
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(directory, ".model-" + Guid.NewGuid().ToString("N") + ".tmp");
    bool created = false;
    try
    {
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
        {
            created = true;
            PackedModelWriter.Write(model, stream);
        }
        File.Move(temporary, target);
        created = false;
    }
    finally
    {
        if (created)
            File.Delete(temporary);
    }
    Console.WriteLine($"{model.Surfaces.Sum(surface => surface.Positions.Length / 3)} triangles, {model.Sprites.Count} sprites, {model.Textures.Count} images -> {target}");
    return 0;
}
catch (Exception error) when (AddonDiagnostics.Annotate(error, AddonStage.Mesh, input))
{
    var issue = AddonDiagnostics.Get(error)!;
    Console.Error.WriteLine($"{issue.Stage}/{issue.Code}: {issue.File} {issue.Detail}");
    return 1;
}
