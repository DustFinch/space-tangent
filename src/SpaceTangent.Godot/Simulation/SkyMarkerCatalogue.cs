// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;

namespace SpaceTangent.Godot.Simulation;

internal sealed record SkyCatalogueMarker(string Name, string Alias, Vector3 Direction, double Detail,
    double Magnitude, double DistanceKiloparsecs);

internal static class SkyMarkerCatalogue
{
    internal static readonly uint[] NavigationalStars =
    [
        677, 2081, 3179, 3419, 7588, 9884, 11767, 13847, 14135, 15863, 21421, 24436, 24608, 25336,
        25428, 26311, 27989, 30438, 32349, 33579, 37279, 37826, 41037, 44816, 45238, 46390, 49669,
        54061, 57632, 59803, 60718, 61084, 62956, 65474, 67301, 68702, 68933, 69673, 71683, 72622,
        72607, 76267, 80763, 82273, 84012, 85927, 86032, 87833, 90185, 91262, 92855, 97649, 100751,
        102098, 107315, 109268, 113368, 113963
    ];

    internal static SkyCatalogueMarker[] Load(string variable, string relativePath, bool pulsars)
    {
        string path = LocalDataPaths.Find(variable, relativePath);
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The sky marker catalogue exceeds 4 MiB.");
            using var reader = new StreamReader(stream);
            return Read(reader, pulsars);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.Print("Sky markers: " + error.Message);
            return [];
        }
    }

    internal static SkyCatalogueMarker[] Read(TextReader reader, bool pulsars)
    {
        var result = new List<SkyCatalogueMarker>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] fields = line.Split('\t');
            if (fields.Length < (pulsars ? 7 : 5) || fields[0].Length == 0)
                continue;
            double ra = Number(fields[1]), dec = Number(fields[2]);
            if (!(ra is >= 0 and < 360 && dec is >= -90 and <= 90))
                continue;
            double detail = Number(fields[3]), value = Number(fields[4]);
            string alias = pulsars && fields[5] != "-" ? fields[5] : "";
            result.Add(new(fields[0], alias, BrightSkyData.Equatorial((float)ra, (float)dec), detail,
                pulsars ? double.NaN : value, pulsars ? value : double.NaN));
        }
        return result.ToArray();
    }

    private static double Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result)
            ? result : double.NaN;
}
