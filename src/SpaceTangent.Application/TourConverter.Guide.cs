// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Application;

public static partial class TourConverter
{
    private static TourConversion ConvertGuide(IReadOnlyList<string> words)
    {
        const int maximumStops = 32;
        var lua = new StringBuilder("local observer = celestia:getobserver()\n");
        var unapplied = new SortedSet<string>(StringComparer.Ordinal);
        int at = 0, count = 0;
        while (at < words.Count)
        {
            if (++count > maximumStops)
                throw new InvalidDataException("The guide contains more than 32 stops.");
            Expect("{");
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (at < words.Count && words[at] != "}")
            {
                string key = Take();
                if (key.Length == 0 || !key.All(char.IsAsciiLetterOrDigit))
                    throw new InvalidDataException("Invalid guide field name.");
                string value = Take();
                if (value is "{" or "}" or "[" or "]" || !fields.TryAdd(key, DecodeValue(value)))
                    throw new InvalidDataException("Invalid or repeated guide field.");
                if (key.ToLowerInvariant() is not ("name" or "target" or "description" or "distance" or "distanceunits"))
                    unapplied.Add("guide field " + key);
            }
            Expect("}");
            if (!fields.TryGetValue("Target", out string? target) || string.IsNullOrWhiteSpace(target))
                throw new InvalidDataException("A guide stop needs a target.");

            string? name = fields.GetValueOrDefault("Name");
            string? description = fields.GetValueOrDefault("Description");
            string caption = string.Join("\n", new[] { name, description }.OfType<string>());
            // Keep the stop name visible when its description exceeds the message limit.
            string notice = caption.Length <= 512 ? caption : name ?? target;
            if (notice.Length > 512)
                throw new InvalidDataException("The guide stop name exceeds the caption limit.");
            if (caption.Length > 512)
                unapplied.Add("guide description at stop " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " exceeds 512 characters");
            double range = 0;
            if (fields.TryGetValue("Distance", out string? distance))
            {
                double scale = (fields.GetValueOrDefault("DistanceUnits") ?? "km").ToLowerInvariant() switch
                {
                    "km" => 1,
                    "au" => AstronomicalUnits.KilometersPerAstronomicalUnit,
                    "ly" => AstronomicalUnits.KilometersPerLightYear,
                    _ => throw new InvalidDataException("Unknown guide distance unit.")
                };
                range = ParseNumber(distance) * scale;
                if (range <= 0 || !double.IsFinite(range))
                    throw new InvalidDataException("The guide distance must be positive and finite.");
            }
            lua.AppendLine($"if st.tryselect({Quote(target)}) then");
            lua.AppendLine(range > 0
                ? $"  observer:gotodistance(st.selection(), {Number(range)}, 1.5)"
                : "  st.gotoobject(1.5, 5)");
            lua.AppendLine("  while st.travelling() do st.wait(0) end");
            if (notice.Length > 0)
                lua.AppendLine($"  st.notice({Quote(notice)}, 3)");
            lua.AppendLine("  st.wait(1.75)");
            lua.AppendLine("end");
        }
        lua.AppendLine("st.notice(\"\", 0)");
        foreach (string note in unapplied.Reverse())
            lua.Insert(0, "-- Unapplied: " + note + "\n");
        return new(lua.ToString(), count, Array.AsReadOnly(unapplied.ToArray()));

        string Take() => at < words.Count ? words[at++] : throw new InvalidDataException("The guide ended early.");
        void Expect(string value)
        {
            if (Take() != value)
                throw new InvalidDataException($"Expected {value} in the guide.");
        }
    }

    private static string DecodeValue(string value) =>
        value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) ?? "" : value;
}
