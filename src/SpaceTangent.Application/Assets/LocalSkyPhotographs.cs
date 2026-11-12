// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application.Assets;

public sealed record LocalSkyPhotograph(string Path, string Credit, string? CreditUrl,
    Vector3d[] Corners, (double U, double V)[] Uvs, Vector3d Center, double RadiusRadians,
    double MinimumResolutionDegrees, double MaximumBrightness, double? BirthJulianDate)
{
    public double Opacity(double pixelDegrees, double limitingMagnitude, double julianDate)
    {
        if (BirthJulianDate is { } birth && julianDate < birth)
            return 0;
        double skyBrightness = 22 - (7.6 - limitingMagnitude) * 1.1;
        return Math.Clamp(MinimumResolutionDegrees / Math.Max(pixelDegrees, 1e-9) - 1, 0, 1) *
            Math.Clamp((skyBrightness - MaximumBrightness) / 2, 0, 1);
    }
}

public static class LocalSkyPhotographs
{
    public static IReadOnlyList<LocalSkyPhotograph> Read(string root)
    {
        using var source = File.OpenRead(Path.Combine(root, "textures.json"));
        if (source.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Sky image metadata exceeds 4 MiB.");
        using var reader = new StreamReader(source);
        using var document = JsonDocument.Parse(reader.ReadToEnd().Replace('\t', ' '),
            new JsonDocumentOptions { MaxDepth = 24, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var result = new List<LocalSkyPhotograph>();
        Collect(document.RootElement, root, result, .05);
        return result.AsReadOnly();
    }

    private static void Collect(JsonElement element, string root, List<LocalSkyPhotograph> result, double parentResolution)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;
        double resolution = Number(element, "minResolution") ?? parentResolution;
        if (element.TryGetProperty("subTiles", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                Collect(child, root, result, resolution);
        if (result.Count >= 8192)
            throw new InvalidDataException("Sky image metadata exceeds 8192 images.");
        if (resolution <= 0 || String(element, "imageUrl") is not { Length: > 0 and <= 120 } file ||
            !file.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ||
            !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            !Polygon(element, "worldCoords", out var polygon))
            return;
        var corners = new Vector3d[4];
        for (int index = 0; index < corners.Length; index++)
        {
            if (!Pair(polygon[index], out double ra, out double dec) || dec is < -90 or > 90)
                return;
            var (sinDec, cosDec) = Math.SinCos(dec * Math.PI / 180);
            var (sinRa, cosRa) = Math.SinCos(Math.IEEERemainder(ra, 360) * Math.PI / 180);
            corners[index] = new(cosDec * cosRa, cosDec * sinRa, sinDec);
        }
        (double U, double V)[] uvs = [(0, 1), (1, 1), (1, 0), (0, 0)];
        if (Polygon(element, "textureCoords", out var texture))
            for (int index = 0; index < uvs.Length; index++)
            {
                if (!Pair(texture[index], out double u, out double v) || u is < 0 or > 1 || v is < 0 or > 1)
                    return;
                uvs[index] = (u, 1 - v);
            }
        Vector3d sum = corners[0] + corners[1] + corners[2] + corners[3];
        if (sum.Length < .01)
            return;
        Vector3d center = sum / sum.Length;
        double radius = corners.Max(corner => Math.Acos(Math.Clamp(Vector3d.Dot(center, corner), -1, 1)));
        if (radius is <= 0 or > Math.PI / 2)
            return;
        string credit = "";
        string? url = null;
        if (element.TryGetProperty("imageCredits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            foreach (var property in credits.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (property.Name.Equals("short", StringComparison.OrdinalIgnoreCase))
                    credit = property.Value.GetString() ?? "";
                if (property.Name.Equals("infoUrl", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(property.Value.GetString(), UriKind.Absolute, out Uri? uri) &&
                    uri.Scheme is "https" or "http")
                    url = uri.AbsoluteUri;
            }
        result.Add(new(Path.Combine(root, file), credit, url, corners, uvs, center, radius, resolution,
            Number(element, "maxBrightness") ?? 13, Number(element, "birthJD")));
    }

    private static bool Polygon(JsonElement element, string key, out JsonElement polygon)
    {
        polygon = default;
        return element.TryGetProperty(key, out var container) && container.ValueKind == JsonValueKind.Array &&
            container.GetArrayLength() == 1 && (polygon = container[0]).ValueKind == JsonValueKind.Array &&
            polygon.GetArrayLength() == 4;
    }

    private static bool Pair(JsonElement element, out double first, out double second)
    {
        first = second = 0;
        return element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2 &&
            element[0].ValueKind == JsonValueKind.Number && element[0].TryGetDouble(out first) && double.IsFinite(first) &&
            element[1].ValueKind == JsonValueKind.Number && element[1].TryGetDouble(out second) && double.IsFinite(second);
    }

    private static string? String(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
}
