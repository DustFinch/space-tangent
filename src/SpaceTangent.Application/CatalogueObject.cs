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
        StellarCatalogue spatialStars, DeepSkyIndex deepSky, ExoplanetCatalogue exoplanets, IReadOnlyList<InstalledSscBody> sscBodies, IEnumerable<SscSurfaceLocation> locations, IEnumerable<SampledMission> missions,
        IReadOnlyDictionary<ObjectId, LocalTleSatellite> localSatellites, EarthPlaceCatalogue earthPlaces)
    {
        var objects = new List<CatalogueObject> { new(SolarSystemSample.SunId, "Sun", CatalogueKind.Star) };
        objects.AddRange(PlanetCatalogue.Planets.Select(planet =>
            new CatalogueObject(planet.Id, planet.Name, CatalogueKind.Planet, SolarSystemSample.SunId)));
        objects.Add(new(SolarSystemSample.MoonId, "Moon", CatalogueKind.Moon, SolarSystemSample.EarthId));
        objects.AddRange(NaturalBodyCatalogue.Bodies.Select(body =>
            new CatalogueObject(body.Id, body.Name, body.Kind, body.Parent)));
        objects.AddRange(satellites.Select(satellite =>
            new CatalogueObject(satellite.Id, satellite.Name, CatalogueKind.Spacecraft, SolarSystemSample.EarthId)
            { Aliases = localSatellites.TryGetValue(satellite.Id, out var local) ? [local.Name, .. local.Aliases] : [] }));
        var scienceIds = satellites.Select(satellite => satellite.Id).ToHashSet();
        objects.AddRange(localSatellites.Values.Where(satellite => !scienceIds.Contains(satellite.Id))
            .Select(satellite => new CatalogueObject(satellite.Id, satellite.Name, CatalogueKind.Spacecraft, SolarSystemSample.EarthId)
                { Aliases = satellite.Aliases }));
        objects.AddRange(sscBodies.Select(body => new CatalogueObject(body.Id, body.Definition.Name, body.Kind, body.Parent)
            { Aliases = Array.AsReadOnly(body.Definition.Names.Split(':').Skip(1).ToArray()) }));
        objects.AddRange(missions.Select(mission =>
            new CatalogueObject(mission.Id, mission.Name, CatalogueKind.Spacecraft, mission.Arcs[0].Center)));
        objects.AddRange(stars.Stars.Select(star =>
        {
            StellarEntry? spatial = spatialStars.Find(star.Id);
            string name = star.Name.Length == 0 ? FormattableString.Invariant($"HIP {star.Hipparcos}") : star.Name;
            return new CatalogueObject(star.Id, name, CatalogueKind.Star, spatial?.BarycenterId)
                { Aliases = spatial?.Aliases ?? [] };
        }));
        objects.AddRange(spatialStars.Stars.Where(star => stars.Find(star.Id) is null)
            .Select(star => new CatalogueObject(star.Id, star.Name, CatalogueKind.Star, star.BarycenterId)
                { Aliases = star.Aliases }));
        objects.AddRange(exoplanets.Hosts.Where(star => spatialStars.Find(star.Id) is null && stars.Find(star.Id) is null)
            .Select(star => new CatalogueObject(star.Id, star.Name, CatalogueKind.Star) { Aliases = star.Aliases }));
        objects.AddRange(exoplanets.Bodies.Select(body => new CatalogueObject(body.Id, body.Name,
            body.Parent == body.Host ? CatalogueKind.Planet : CatalogueKind.Moon, body.Parent) { Aliases = body.Names }));
        objects.AddRange(deepSky.Objects.Select(body => new CatalogueObject(body.Id,
            body.Name == body.Designation ? body.Name : $"{body.Name} ({body.Designation})", body.Kind switch
            {
                DeepSkyKind.Galaxy => CatalogueKind.Galaxy,
                DeepSkyKind.Nebula => CatalogueKind.Nebula,
                _ => CatalogueKind.Cluster
            }) { Aliases = body.Aliases }));
        objects.AddRange(earthPlaces.Places.Select(place =>
            new CatalogueObject(place.Id, place.EnglishName, CatalogueKind.SurfaceLocation, SolarSystemSample.EarthId)
            {
                Aliases = new[] { place.Name, place.Name.Replace('ё', 'е').Replace('Ё', 'Е') }
                    .Where(alias => alias != place.EnglishName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            }));
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

        var ranked = new List<CatalogueObject>[] { [], [], [] };
        for (int index = 0; index < Objects.Count; index++)
        {
            CatalogueObject body = Objects[index];
            int rank = Math.Min(Rank(body.Name), Rank(body.Id.Value));
            for (int alias = 0; rank != 0 && alias < body.Aliases.Count; alias++)
                rank = Math.Min(rank, Rank(body.Aliases[alias]));
            if (rank < ranked.Length)
                ranked[rank].Add(body);
        }
        return [.. ranked[0], .. ranked[1], .. ranked[2]];

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
