// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal static class PlanetMoonModels
{
    internal static IReadOnlyDictionary<ObjectId, ArrayMesh> Load()
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
        return models;
    }
}
