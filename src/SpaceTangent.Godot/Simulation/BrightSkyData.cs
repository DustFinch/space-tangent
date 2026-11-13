// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Globalization;
using SpaceTangent.Core.Observing;
using System.Text.Json;

namespace SpaceTangent.Godot.Simulation;

internal sealed record BrightSkyData(BrightStarCatalogue Catalogue, IReadOnlyList<(Vector3 From, Vector3 To)> Figures, TransientStarCatalogue Transients, MeteorCatalogue Meteors, SkyCultureLibrary Cultures, SkySurveyTiles Surveys)
{
    internal IReadOnlyList<(Vector3 From, Vector3 To)> Boundaries { get; init; } = [];

    private readonly Dictionary<string, Texture2D?> _artTextures = new(StringComparer.Ordinal);
    private const int MaximumArtTextures = 128;
    private const int MaximumArtPixels = 24 * 1024 * 1024;
    private int _artPixels;

    internal Texture2D? ArtTexture(SkyCultureArtwork art)
    {
        if (_artTextures.TryGetValue(art.ImagePath, out var cached))
            return cached;
        if (_artTextures.Count >= MaximumArtTextures)
        {
            _artTextures.Clear();
            _artPixels = 0;
        }
        try
        {
            using FileStream source = File.OpenRead(art.ImagePath);
            if (source.Length is < 16 or > 8 * 1024 * 1024)
                throw new InvalidDataException("The artwork exceeds its size limit.");
            var bytes = new byte[(int)source.Length];
            source.ReadExactly(bytes);
            using var image = new Image();
            Error status = Path.GetExtension(art.ImagePath).ToLowerInvariant() switch
            {
                ".webp" => image.LoadWebpFromBuffer(bytes),
                ".png" => image.LoadPngFromBuffer(bytes),
                _ => image.LoadJpgFromBuffer(bytes)
            };
            if (status != Error.Ok || image.IsEmpty() || image.GetWidth() > 4096 || image.GetHeight() > 4096)
                throw new InvalidDataException("The artwork image is unreadable or too large.");
            int pixels = image.GetWidth() * image.GetHeight();
            if (_artPixels + pixels > MaximumArtPixels)
            {
                _artTextures.Clear();
                _artPixels = 0;
            }
            _artPixels += pixels;
            image.GenerateMipmaps();
            return _artTextures[art.ImagePath] = ImageTexture.CreateFromImage(image);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.Print($"Constellation artwork {Path.GetFileName(art.ImagePath)}: {error.Message}");
            return _artTextures[art.ImagePath] = null;
        }
    }

    internal static BrightSkyData Load()
    {
        using var catalogue = new StringReader(global::Godot.FileAccess.GetFileAsString("res://SkyData/bright-stars.tsv"));
        BrightStarCatalogue stars = BrightStarCatalogue.Read(catalogue);
        using var geoJson = JsonDocument.Parse(global::Godot.FileAccess.GetFileAsString("res://SkyData/constellation-lines.json"));
        var lines = new List<(Vector3, Vector3)>();
        foreach (JsonElement feature in geoJson.RootElement.GetProperty("features").EnumerateArray())
        foreach (JsonElement figure in feature.GetProperty("geometry").GetProperty("coordinates").EnumerateArray())
        {
            Vector3? previous = null;
            foreach (JsonElement vertex in figure.EnumerateArray())
            {
                Vector3 direction = Equatorial(vertex[0].GetSingle(), vertex[1].GetSingle());
                if (previous is { } start)
                    AddArc(start, direction, lines);
                previous = direction;
            }
        }
        string root = LocalDataPaths.Find("SPACETANGENT_TRANSIENT_ROOT", "sky/transients", directory: true);
        var transients = TransientStarCatalogue.Load(root, message => GD.PushWarning("Transient stars: " + message));
        using var showers = new StringReader(global::Godot.FileAccess.GetFileAsString("res://SkyData/meteor-showers.tsv"));
        string cultures = LocalDataPaths.Find("SPACETANGENT_SKY_CULTURES", "sky/cultures", directory: true);
        int missingArtwork = 0;
        var library = SkyCultureLibrary.Load(cultures, message =>
        {
            if (message.StartsWith("Constellation artwork ", StringComparison.Ordinal))
                missingArtwork++;
            else
                GD.Print("Sky cultures: " + message);
        });
        if (missingArtwork > 0)
            GD.Print($"Sky cultures: {missingArtwork} referenced illustrations are absent from the local archive.");
        string surveys = LocalDataPaths.Find("SPACETANGENT_SKY_SURVEYS", "sky/surveys", directory: true);
        string photographs = LocalDataPaths.Find("SPACETANGENT_SKY_PHOTOGRAPHS", "sky/nebula-images", directory: true);
        return new(stars, lines.AsReadOnly(), transients, MeteorCatalogue.Read(showers), library, new(surveys, photographs))
        {
            Boundaries = LoadBoundaries()
        };
    }

    private static IReadOnlyList<(Vector3, Vector3)> LoadBoundaries()
    {
        string path = LocalDataPaths.Find("SPACETANGENT_CONSTELLATION_BOUNDARIES", "sky/boundaries.txt");
        if (path.Length == 0)
            return [];
        try
        {
            using var source = File.OpenRead(path);
            if (source.Length > 1024 * 1024)
                throw new InvalidDataException("The catalogue exceeds its size limit.");
            using var reader = new StreamReader(source);
            var lines = ReadBoundaries(reader);
            GD.Print($"Constellation boundaries loaded: {lines.Count} segments.");
            return lines;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.Print($"Constellation boundaries: {error.Message}");
            return [];
        }
    }

    private static IReadOnlyList<(Vector3, Vector3)> ReadBoundaries(TextReader reader)
    {
        var lines = new List<(Vector3, Vector3)>();
        string? previousGroup = null;
        Vector3 previous = default;
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 ||
                !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float hours) ||
                !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float degrees) ||
                !(hours is >= 0 and <= 24) || !(degrees is >= -90 and <= 90))
                throw new InvalidDataException("Invalid constellation boundary vertex.");
            string group = fields[2];
            if (group == "XXX")
                break;
            Vector3 direction = Equatorial(hours * 15, degrees);
            if (previousGroup == group)
                lines.Add((previous, direction));
            previousGroup = group;
            previous = direction;
        }
        return lines.AsReadOnly();
    }

    internal static Vector3 Equatorial(float raDegrees, float decDegrees)
    {
        var (sinRa, cosRa) = MathF.SinCos(Mathf.DegToRad(raDegrees));
        var (sinDec, cosDec) = MathF.SinCos(Mathf.DegToRad(decDegrees));
        return new(cosDec * cosRa, cosDec * sinRa, sinDec);
    }

    internal static void AddArc(Vector3 from, Vector3 to, List<(Vector3, Vector3)> lines)
    {
        float angle = from.AngleTo(to);
        if (angle < 1e-5f || angle >= MathF.PI - 1e-5f)
            return;
        int steps = (int)MathF.Ceiling(angle / Mathf.DegToRad(5));
        for (int step = 0; step < steps; step++)
            lines.Add((from.Slerp(to, (float)step / steps), from.Slerp(to, (float)(step + 1) / steps)));
    }
}
