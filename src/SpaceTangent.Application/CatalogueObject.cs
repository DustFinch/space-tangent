// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

public enum CatalogueKind { Star, Planet, Moon, Spacecraft, DwarfPlanet, Asteroid, Comet, Galaxy, Nebula, Cluster, SurfaceLocation }

public sealed record CatalogueObject(ObjectId Id, string Name, CatalogueKind Kind, ObjectId? Parent = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed class ObjectCatalogue
{
    internal ObjectCatalogue(IReadOnlyList<ScienceSatellite> satellites, BrightStarCatalogue stars,
        StellarCatalogue spatialStars, IReadOnlyList<InstalledSscBody> sscBodies, IEnumerable<SscSurfaceLocation> locations)
    {
        var objects = new List<CatalogueObject> { new(SolarSystemSample.SunId, "Sun", CatalogueKind.Star) };
        objects.AddRange(PlanetCatalogue.Planets.Select(planet =>
            new CatalogueObject(planet.Id, planet.Name, CatalogueKind.Planet, SolarSystemSample.SunId)));
        objects.Add(new(SolarSystemSample.MoonId, "Moon", CatalogueKind.Moon, SolarSystemSample.EarthId));
        objects.AddRange(NaturalBodyCatalogue.Bodies.Select(body =>
            new CatalogueObject(body.Id, body.Name, body.Kind, body.Parent)));
        objects.AddRange(satellites.Select(satellite =>
            new CatalogueObject(satellite.Id, satellite.Name, CatalogueKind.Spacecraft, SolarSystemSample.EarthId)));
        objects.AddRange(sscBodies.Select(body => new CatalogueObject(body.Id, body.Definition.Name, body.Kind, body.Parent)
            { Aliases = Array.AsReadOnly(body.Definition.Names.Split(':').Skip(1).ToArray()) }));
        objects.AddRange(stars.Stars.Select(star =>
            new CatalogueObject(star.Id, star.Name.Length == 0 ? FormattableString.Invariant($"HIP {star.Hipparcos}") : star.Name,
                CatalogueKind.Star)));
        objects.AddRange(spatialStars.Stars.Where(star => stars.Find(star.Id) is null)
            .Select(star => new CatalogueObject(star.Id, star.Name, CatalogueKind.Star)));
        objects.AddRange(DeepSkyCatalogue.Objects.Select(body => new CatalogueObject(body.Id,
            $"{body.Name} ({body.Designation})", body.Kind switch
            {
                DeepSkyKind.Galaxy => CatalogueKind.Galaxy,
                DeepSkyKind.Nebula => CatalogueKind.Nebula,
                _ => CatalogueKind.Cluster
            })));
        Objects = objects.AsReadOnly();
        _byId = objects.ToDictionary(body => body.Id);
        SurfaceSites = SurfaceSite.Bind(this, locations);
        foreach (SurfaceSite site in SurfaceSites)
        {
            var location = new CatalogueObject(site.Id, site.Definition.Name, CatalogueKind.SurfaceLocation, site.Body);
            objects.Add(location);
            _byId.Add(location.Id, location);
        }
    }

    private readonly Dictionary<ObjectId, CatalogueObject> _byId;
    public IReadOnlyList<CatalogueObject> Objects { get; }
    internal IReadOnlyList<SurfaceSite> SurfaceSites { get; }
    public CatalogueObject? Find(ObjectId id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<CatalogueObject> Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return Objects;

        return Objects.Select(body => (Body: body, Rank: RankBody(body)))
            .Where(result => result.Rank < 3)
            .OrderBy(result => result.Rank)
            .Select(result => result.Body)
            .ToArray();

        int RankBody(CatalogueObject body)
        {
            int rank = Math.Min(Rank(body.Name), Rank(body.Id.Value));
            foreach (string alias in body.Aliases)
                rank = Math.Min(rank, Rank(alias));
            return rank;
        }

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
