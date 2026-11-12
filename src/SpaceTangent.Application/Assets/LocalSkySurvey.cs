// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application.Assets;

public sealed record LocalSkySurvey(string Id, string Title, string Root, Basis3d ToEquatorial,
    int MaximumOrder, int TileWidth, string Extension)
{
    public const int AllSkyOrder = 3, AllSkyColumns = 27;
    public const int MaximumViewCells = 160;

    public static LocalSkySurvey Read(string root)
    {
        root = Path.GetFullPath(root);
        string id = Path.GetFileName(root);
        if (!ValidId(id))
            throw new InvalidDataException("A survey directory needs a lowercase identifier.");
        using var stream = AddonDependencies.OpenRead(Path.Combine(root, "properties"), 64 * 1024);
        if (stream.Length > 64 * 1024)
            throw new InvalidDataException("The survey properties file exceeds 64 KiB.");
        using var reader = new StreamReader(stream);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } row)
        {
            row = row.Trim();
            int equals = row.IndexOf('=');
            if (row.StartsWith('#') || equals < 1)
                continue;
            values[row[..equals].Trim()] = row[(equals + 1)..].Trim();
        }

        Basis3d axes = Required("hips_frame").ToLowerInvariant() switch
        {
            "equatorial" => Basis3d.Identity,
            "galactic" => GalacticLight.ToEquatorial,
            _ => throw new InvalidDataException("The survey frame must be equatorial or galactic.")
        };
        string[] formats = Required("hips_tile_format").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string extension = formats.Contains("jpeg", StringComparer.OrdinalIgnoreCase) ? "jpg" :
            formats.Contains("png", StringComparer.OrdinalIgnoreCase) ? "png" :
            throw new InvalidDataException("The survey needs JPEG or PNG tiles.");
        int width = values.ContainsKey("hips_tile_width") ? Integer("hips_tile_width", 16, 4096) : 512;
        if ((width & (width - 1)) != 0)
            throw new InvalidDataException("The survey tile width must be a power of two.");
        return new(id, values.GetValueOrDefault("obs_title", id), root, axes,
            Integer("hips_order", AllSkyOrder, HealpixSkyGrid.MaximumOrder), width, extension);

        string Required(string key) => values.TryGetValue(key, out var value) && value.Length != 0
            ? value : throw new InvalidDataException($"Missing survey property {key}.");
        int Integer(string key, int min, int max) => int.TryParse(Required(key), NumberStyles.None,
            CultureInfo.InvariantCulture, out int value) && value >= min && value <= max
            ? value : throw new InvalidDataException($"Unsupported survey property {key}.");
    }

    public string AllSkyPath => Path.Combine(Root, "Norder3", $"Allsky.{Extension}");

    public string TilePath(int order, long cell)
    {
        if (order < AllSkyOrder || order > MaximumOrder || cell < 0 || cell >= HealpixSkyGrid.Count(order))
            throw new ArgumentOutOfRangeException(nameof(cell));
        return Path.Combine(Root, FormattableString.Invariant($"Norder{order}"),
            FormattableString.Invariant($"Dir{cell / 10_000 * 10_000}"), FormattableString.Invariant($"Npix{cell}.{Extension}"));
    }

    public int OrderFor(double verticalFieldDegrees, int heightPixels)
    {
        if (!double.IsFinite(verticalFieldDegrees) || verticalFieldDegrees <= 0 || heightPixels < 1)
            throw new ArgumentOutOfRangeException(nameof(verticalFieldDegrees));
        double order = Math.Ceiling(Math.Log2(58.6 * heightPixels / (verticalFieldDegrees * TileWidth)));
        return (int)Math.Clamp(order, AllSkyOrder, MaximumOrder);
    }

    public static bool ValidId(string id) => id.Length is > 0 and <= 64 &&
        id.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public static bool ValidAllSky(int width, int height) => width / AllSkyColumns >= 2 &&
        height >= width / AllSkyColumns * 29;

    public static (double U, double V) TileUv(int order, long cell, int sourceOrder, int size,
        double x, double y)
    {
        int depth = order - sourceOrder;
        if (depth is < 0 or > HealpixSkyGrid.MaximumOrder || size < 2)
            throw new ArgumentOutOfRangeException(nameof(sourceOrder));
        long within = cell & ((1L << (2 * depth)) - 1);
        var (_, subX, subY) = HealpixSkyGrid.Coordinates(depth, within);
        double scale = 1d / (1L << depth), inset = .5 / size;
        return (Math.Clamp((subY + y) * scale, inset, 1 - inset),
            Math.Clamp((subX + x) * scale, inset, 1 - inset));
    }

    public static (double U, double V) AllSkyUv(int order, long cell, int width, int height, double x, double y)
    {
        if (!ValidAllSky(width, height))
            throw new ArgumentOutOfRangeException(nameof(width));
        int size = width / AllSkyColumns;
        long parent = cell >> (2 * (order - AllSkyOrder));
        var (u, v) = TileUv(order, cell, AllSkyOrder, size, x, y);
        return ((parent % AllSkyColumns + u) * size / width, (parent / AllSkyColumns + v) * size / height);
    }
}
