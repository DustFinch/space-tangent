// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal sealed record OrbitAtmosphereTextures(ImageTexture Transmission, ImageTexture3D Inscatter,
    float RadiusKilometers, float HeightKilometers, float MieAsymmetry, Vector3 MieRatios)
{
    internal static IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> LoadAll()
    {
        var textures = new Dictionary<ObjectId, OrbitAtmosphereTextures>();
        string root = LocalDataPaths.Find("SPACETANGENT_ATMOSPHERE_ROOT", "atmospheres", directory: true);
        foreach (var (id, name) in new[] { (SolarSystemSample.EarthId, "earth"), (PlanetCatalogue.MarsId, "mars"), (new ObjectId("sol/titan"), "titan") })
        {
            string file = Path.Combine(root, $"builtin-original-{name}.atmscat");
            if (!File.Exists(file))
                continue;
            try
            {
                textures.Add(id, Create(AtmosphereLookup.Load(file)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                GD.PushWarning($"Could not load the {name} atmosphere lookup. {error.Message}");
            }
        }
        return textures;
    }

    private static OrbitAtmosphereTextures Create(AtmosphereLookup lookup)
    {
        using Image transmission = Image.CreateFromData(lookup.TransmissionWidth, lookup.TransmissionHeight, false,
            Image.Format.Rgbh, lookup.TransmissionHalfBytes());
        var layers = new global::Godot.Collections.Array<Image>();
        var volume = new ImageTexture3D();
        try
        {
            int stride = lookup.ScatteringWidth * lookup.ScatteringHeight * 8;
            byte[] data = lookup.ScatteringHalfBytes();
            for (int slice = 0; slice < lookup.ScatteringDepth; slice++)
                layers.Add(Image.CreateFromData(lookup.ScatteringWidth, lookup.ScatteringHeight, false, Image.Format.Rgbah,
                    data.AsSpan(slice * stride, stride).ToArray()));
            if (volume.Create(Image.Format.Rgbah, lookup.ScatteringWidth, lookup.ScatteringHeight, lookup.ScatteringDepth, false, layers) != Error.Ok)
                throw new InvalidDataException("The renderer rejected the atmosphere inscatter volume.");
            Vector3d coefficients = lookup.RayleighPerKilometer;
            return new(ImageTexture.CreateFromImage(transmission), volume, lookup.RadiusKilometers, lookup.TransparentHeightKilometers,
                lookup.MieAsymmetry, new(Ratio(coefficients.X, coefficients.X), Ratio(coefficients.X, coefficients.Y),
                    Ratio(coefficients.X, coefficients.Z)));
        }
        catch
        {
            volume.Dispose();
            throw;
        }
        finally
        {
            foreach (Image layer in layers)
                layer.Dispose();
        }

        static float Ratio(double first, double second) => second > 0 && double.IsFinite(first / second) &&
            first / second <= float.MaxValue ? (float)(first / second) : 0;
    }

    internal void Bind(ShaderMaterial material)
    {
        material.SetShaderParameter("use_atmosphere_lookup", true);
        material.SetShaderParameter("air_transmission", Transmission);
        material.SetShaderParameter("air_inscatter", Inscatter);
        material.SetShaderParameter("air_lookup_height_km", HeightKilometers);
        material.SetShaderParameter("air_mie_asymmetry", MieAsymmetry);
        material.SetShaderParameter("air_mie_ratios", MieRatios);
    }
}
