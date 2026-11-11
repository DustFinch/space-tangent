// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Observing;
using System.Text.Json;

namespace SpaceTangent.Godot.Simulation;

internal sealed record BrightSkyData(BrightStarCatalogue Catalogue, IReadOnlyList<(Vector3 From, Vector3 To)> Figures)
{
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
        return new(stars, lines.AsReadOnly());
    }

    internal static Vector3 Equatorial(float raDegrees, float decDegrees)
    {
        var (sinRa, cosRa) = MathF.SinCos(Mathf.DegToRad(raDegrees));
        var (sinDec, cosDec) = MathF.SinCos(Mathf.DegToRad(decDegrees));
        return new(cosDec * cosRa, cosDec * sinRa, sinDec);
    }

    private static void AddArc(Vector3 from, Vector3 to, List<(Vector3, Vector3)> lines)
    {
        float angle = from.AngleTo(to);
        if (angle < 1e-5f || angle >= MathF.PI - 1e-5f)
            return;
        int steps = (int)MathF.Ceiling(angle / Mathf.DegToRad(5));
        for (int step = 0; step < steps; step++)
            lines.Add((from.Slerp(to, (float)step / steps), from.Slerp(to, (float)(step + 1) / steps)));
    }
}
