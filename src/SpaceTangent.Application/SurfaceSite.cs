// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application;

/// <summary>A named site in the published parent body's fixed frame.</summary>
public sealed record SurfaceSite(ObjectId Id, ObjectId Body, SscSurfaceLocation Definition, Vector3d OffsetKilometers,
    Vector3d Radial, Vector3d North, double ScaleKilometers)
{
    internal static IReadOnlyList<SurfaceSite> Bind(ObjectCatalogue catalogue, IEnumerable<SscSurfaceLocation> locations)
    {
        var sites = new List<SurfaceSite>();
        var usedIds = new HashSet<ObjectId>();
        foreach (var group in locations.GroupBy(site => site.BodyPath, StringComparer.OrdinalIgnoreCase))
        {
            ObjectId body;
            try { body = SceneLink.ResolveObject(group.Key, catalogue); }
            catch (InvalidDataException) { continue; }
            double radius = NavigationCamera.BodyRadiusKilometers(body);
            double pole = PlanetCatalogue.Find(body)?.PolarRadiusKilometers ?? radius;
            foreach (SscSurfaceLocation site in group)
            {
                if (site.Type == "Ring")
                    continue;
                string slug = string.Join('-', site.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word => new string(word.Where(char.IsAsciiLetterOrDigit).ToArray())).Where(word => word.Length > 0));
                ObjectId id = new($"location/{body.Value}/{(slug.Length == 0 ? "site" : slug)}");
                if (!usedIds.Add(id))
                {
                    string key = string.Create(CultureInfo.InvariantCulture,
                        $"{site.Name}|{site.LongitudeDegrees:R}|{site.LatitudeDegrees:R}|{site.AltitudeKilometers:R}");
                    string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..12];
                    id = new(id.Value + "-" + suffix);
                    if (!usedIds.Add(id))
                        continue;
                }
                var (sinLongitude, cosLongitude) = Math.SinCos(site.LongitudeDegrees * Math.PI / 180);
                var (sinLatitude, cosLatitude) = Math.SinCos(site.LatitudeDegrees * Math.PI / 180);
                var radial = new Vector3d(cosLatitude * cosLongitude, cosLatitude * sinLongitude, sinLatitude);
                var north = new Vector3d(-sinLatitude * cosLongitude, -sinLatitude * sinLongitude, cosLatitude);
                double surfaceRadius = 1 / Math.Sqrt(Math.Pow(cosLatitude / radius, 2) + Math.Pow(sinLatitude / pole, 2));
                double scale = Math.Clamp(site.SizeKilometers / 2, .01, Math.Max(.01, radius / 12));
                sites.Add(new(id, body, site, radial * (surfaceRadius + site.AltitudeKilometers), radial, north, scale));
            }
        }
        return sites.AsReadOnly();
    }

    internal NavigationPlacement Approach(double range) => new(Radial * range, Radial * -1, North)
        { Frame = NavigationFrame.Surface, Reference = Body };
}
