// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal static class PlanetMoonModels
{
    private static readonly (string Name, string File, double Radius, Vector3d Offset)[] NormalizedShapes =
    [
        ("mimas", "mimas.cmod", 206.6, default),
        ("hyperion", "hyperion.cmod", 180.9, new(3.792, 2.103, 4.191)),
        ("phoebe", "phoebe.cmod", 109.4, default),
        ("proteus", "proteus.cmod", 212, default),
        ("nereid", "roughsphere.cms", 172.5, default)
    ];

    internal static IReadOnlyDictionary<ObjectId, ArrayMesh> Load(PlanetTextures? textures)
    {
        string root = LocalDataPaths.Find("SPACETANGENT_PLANET_MODELS", "planets/models", directory: true);
        var models = new Dictionary<ObjectId, ArrayMesh>();
        foreach (string name in new[] { "phobos", "deimos" })
        {
            string original = Path.Combine(root, name + ".cmod");
            string packed = Path.Combine(root, name + ".glb");
            bool source = File.Exists(original);
            if (!source && !File.Exists(packed))
                continue;
            var model = source ? CmodMeshDecoder.DecodeNativeBody(root, original) : PackedModelDecoder.Decode(packed);
            models.Add(new("sol/" + name), ScienceMeshLoader.Publish(model, source ? original : packed, litBody: true));
        }
        foreach (var (name, file, radius, offset) in NormalizedShapes)
        {
            string path = Path.Combine(root, file);
            if (!File.Exists(path))
                continue;
            var source = file.EndsWith(".cms", StringComparison.Ordinal)
                ? DisplacedSphereDecoder.Decode(path) : CmodMeshDecoder.DecodeNativeBody(root, path);
            var id = new ObjectId("sol/" + name);
            Texture2D? map = textures?.Surfaces.GetValueOrDefault(id);
            models.Add(id, ScienceMeshLoader.Publish(Normalize(source, radius, offset), path, litBody: true, bodySurface: map));
        }
        return models;
    }

    private static DecodedScienceModel Normalize(DecodedScienceModel source, double radius, Vector3d offset)
    {
        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3d max = min * -1;
        foreach (var surface in source.Surfaces)
        foreach (var p in surface.Positions.Span)
        {
            min = new(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }
        var span = max - min;
        double extent = Math.Max(span.X, Math.Max(span.Y, span.Z));
        if (!double.IsFinite(extent) || extent <= 0)
            throw new InvalidDataException("A normalized planetary model has no usable extent.");
        Vector3d center = (min + max) * .5 + offset;
        double scale = 2 * radius / extent;
        var surfaces = new List<DecodedSurface>(source.Surfaces.Count);
        foreach (var surface in source.Surfaces)
        {
            var positions = surface.Positions.ToArray();
            var normals = surface.Normals.ToArray();
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = ToPoleZ((positions[i] - center) * scale);
                normals[i] = ToPoleZ(normals[i]);
            }
            surfaces.Add(surface with { Positions = positions, Normals = normals });
        }
        return source with { Surfaces = surfaces.AsReadOnly(), Diameter = span.Length * scale };

        static Vector3d ToPoleZ(Vector3d vector) => new(vector.X, -vector.Z, vector.Y);
    }
}
