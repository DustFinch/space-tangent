// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public static class LegacySavedEarthPlaces
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>Appends valid archived sites while retaining existing user entries with the same ID.</summary>
    public static int Merge(string destination, IReadOnlyList<EarthPlace> archived) => Write(destination, archived);

    public static void Save(string destination, string name, EarthSite location)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80 || name.Any(char.IsControl) || !location.IsValid() || location.HeightKilometers > 100)
            throw new InvalidDataException("A saved site needs a name and valid Earth coordinates.");
        double offset = Math.Round(location.LongitudeDegrees / 15);
        var site = EarthPlaceCatalogue.ParseSavedText(FormattableString.Invariant(
            $"{name}\t{location.LatitudeDegrees:R}\t{location.LongitudeDegrees:R}\t{location.HeightKilometers * 1000:R}\t{offset:R}")).Single();
        Write(destination, [site], name);
    }

    public static bool Remove(string destination, string name)
    {
        name = name.Trim();
        return name.Length is > 0 and <= 80 && !name.Any(char.IsControl) && Write(destination, [], name) > 0;
    }

    private static int Write(string destination, IReadOnlyList<EarthPlace> archived, string? replacedName = null)
    {
        if (archived.Count == 0 && replacedName is null)
            return 0;
        string target = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        using var gate = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);

        string previous = "";
        if (File.Exists(target))
        {
            using var file = File.OpenRead(target);
            if (file.Length > MaximumBytes)
                throw new InvalidDataException("The saved Earth place file exceeds 4 MiB.");
            using var reader = new StreamReader(file, Utf8);
            previous = reader.ReadToEnd();
        }
        bool removed = false;
        if (replacedName is not null)
        {
            if (archived.Count > 0)
            {
                var update = archived[0];
                if (EarthPlaceCatalogue.ParseSavedText(previous).FirstOrDefault(site => site.Id == update.Id) is { } old)
                    archived = [update with { TimeZone = old.TimeZone, FixedOffsetHours = old.FixedOffsetHours }];
            }
            using var lines = new StringReader(previous);
            var remaining = new StringBuilder();
            while (lines.ReadLine() is { } line)
            {
                if (!line.TrimStart().StartsWith('#') &&
                    line.Split('\t', 2)[0].Trim().Equals(replacedName, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }
                remaining.AppendLine(line);
            }
            previous = remaining.ToString();
        }
        var present = EarthPlaceCatalogue.ParseSavedText(previous).Select(site => site.Id).ToHashSet();
        var added = new List<string>();
        foreach (EarthPlace site in archived)
        {
            if (site.Name.Length == 0 || site.Name.Any(char.IsControl) || site.TimeZone.Any(char.IsControl) ||
                site.FixedOffsetHours is not (>= -14 and <= 14) || site.LatitudeDegrees is not (>= -90 and <= 90) ||
                site.LongitudeDegrees is not (>= -180 and <= 180) || site.AltitudeMeters is not (>= -100_000 and <= 100_000) ||
                !present.Add(site.Id))
                continue;
            string text = FormattableString.Invariant($"{site.Name}\t{site.LatitudeDegrees:R}\t{site.LongitudeDegrees:R}\t{site.AltitudeMeters:R}\t{site.FixedOffsetHours.Value:R}");
            added.Add(site.TimeZone.Length == 0 ? text : text + "\t" + site.TimeZone);
        }
        if (added.Count == 0 && !removed)
            return 0;
        if (present.Count > 16_384)
            throw new InvalidDataException("The saved Earth place file exceeds 16,384 distinct entries.");
        var updated = new StringBuilder(previous);
        if (updated.Length > 0 && updated[^1] is not ('\r' or '\n'))
            updated.AppendLine();
        foreach (string site in added)
            updated.AppendLine(site);
        byte[] bytes = Utf8.GetBytes(updated.ToString());
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The combined saved Earth place file exceeds 4 MiB.");

        string temp = Path.Combine(directory, ".saved-places-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temp);
        }
        return removed && added.Count == 0 ? 1 : added.Count;
    }
}
