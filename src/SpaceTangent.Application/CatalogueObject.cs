// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public enum CatalogueKind { Star, Planet, Moon, Spacecraft }

public sealed record CatalogueObject(ObjectId Id, string Name, CatalogueKind Kind, ObjectId? Parent = null);

public sealed class ObjectCatalogue
{
    internal ObjectCatalogue(IReadOnlyList<ScienceSatellite> satellites, BrightStarCatalogue stars)
    {
        var objects = new List<CatalogueObject> { new(SolarSystemSample.SunId, "Sun", CatalogueKind.Star) };
        objects.AddRange(PlanetCatalogue.Planets.Select(planet =>
            new CatalogueObject(planet.Id, planet.Name, CatalogueKind.Planet, SolarSystemSample.SunId)));
        objects.Add(new(SolarSystemSample.MoonId, "Moon", CatalogueKind.Moon, SolarSystemSample.EarthId));
        objects.AddRange(satellites.Select(satellite =>
            new CatalogueObject(satellite.Id, satellite.Name, CatalogueKind.Spacecraft, SolarSystemSample.EarthId)));
        objects.AddRange(stars.Stars.Select(star =>
            new CatalogueObject(star.Id, star.Name.Length == 0 ? FormattableString.Invariant($"HIP {star.Hipparcos}") : star.Name,
                CatalogueKind.Star)));
        Objects = objects.AsReadOnly();
        _byId = objects.ToDictionary(body => body.Id);
    }

    private readonly Dictionary<ObjectId, CatalogueObject> _byId;
    public IReadOnlyList<CatalogueObject> Objects { get; }
    public CatalogueObject? Find(ObjectId id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<CatalogueObject> Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return Objects;

        return Objects.Select(body => (Body: body, Rank: Math.Min(Rank(body.Name), Rank(body.Id.Value))))
            .Where(result => result.Rank < 3)
            .OrderBy(result => result.Rank)
            .Select(result => result.Body)
            .ToArray();

        int Rank(string value)
        {
            const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
            if (value.Equals(query, comparison))
                return 0;
            if (value.StartsWith(query, comparison))
                return 1;
            return value.Contains(query, comparison) ? 2 : 3;
        }
    }
}
