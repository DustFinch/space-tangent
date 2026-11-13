// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

/// <summary>Per-observer filters using the archived kind mask and catalogue designations.</summary>
public readonly record struct DeepSkyVisibility(int Kinds, string? Catalogues)
{
    public static DeepSkyVisibility All => new(15, null);
    private static readonly string[] KnownCatalogues = ["M", "C", "NGC", "IC", "UGC", "Collinder"];

    internal static string? NormalizeCatalogues(string value)
    {
        string[] names = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string catalogues = string.Join(',', KnownCatalogues.Where(name => names.Contains(name, StringComparer.Ordinal)));
        return catalogues.Length > 0 ? catalogues : null;
    }

    internal bool IsValid() => Kinds is >= 0 and <= 15 &&
        (Catalogues is null || Catalogues.Length <= 32 && Catalogues == NormalizeCatalogues(Catalogues));

    public bool Allows(DeepSkyObject body)
    {
        int bit = body.Kind switch
        {
            DeepSkyKind.Galaxy => 1,
            DeepSkyKind.GlobularCluster => 2,
            DeepSkyKind.OpenCluster => 4,
            DeepSkyKind.Nebula => 8,
            _ => 0
        };
        if ((Kinds & bit) == 0)
            return false;
        if (Catalogues is null)
            return true;

        ReadOnlySpan<char> remaining = Catalogues.AsSpan();
        Span<Range> prefixes = stackalloc Range[6];
        int count = remaining.Split(prefixes, ',');
        foreach (Range prefix in prefixes[..count])
        {
            var catalogue = remaining[prefix];
            if (Matches(body.Designation, catalogue) || Matches(body.Name, catalogue))
                return true;
            foreach (string alias in body.Aliases)
                if (Matches(alias, catalogue))
                    return true;
        }
        return false;
    }

    private static bool Matches(ReadOnlySpan<char> name, ReadOnlySpan<char> prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        ReadOnlySpan<char> suffix = name[prefix.Length..].TrimStart(' ');
        if (suffix.IsEmpty || !char.IsAsciiDigit(suffix[0]))
            return false;
        return !prefix.SequenceEqual("C") || int.TryParse(suffix, out int number) && number is >= 1 and <= 109 &&
            char.IsAsciiDigit(suffix[^1]);
    }
}
