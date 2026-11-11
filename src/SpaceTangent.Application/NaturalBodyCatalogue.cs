// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

public sealed record NaturalBodyDefinition(ObjectId Id, string Name, CatalogueKind Kind, ObjectId Parent,
    int NaifId, double RadiusKilometers, string Color, string? Texture, string? SampledOrbit = null);

public static class NaturalBodyCatalogue
{
    public static ObjectId CometId { get; } = new("sol/67p");
    public static ObjectId PlutoId { get; } = new("sol/pluto");

    public static IReadOnlyList<NaturalBodyDefinition> Bodies { get; } = Array.AsReadOnly(new NaturalBodyDefinition[]
    {
        new(new("sol/phobos"), "Phobos", CatalogueKind.Moon, PlanetCatalogue.MarsId, 401, 11.1, "a69580", "phobos.jpg"),
        new(new("sol/deimos"), "Deimos", CatalogueKind.Moon, PlanetCatalogue.MarsId, 402, 6.2, "bca38b", "deimos.jpg"),
        new(new("sol/io"), "Io", CatalogueKind.Moon, new("sol/jupiter"), 501, 1821.6, "f1d385", "io.jpg"),
        new(new("sol/europa"), "Europa", CatalogueKind.Moon, new("sol/jupiter"), 502, 1560.8, "dbc7a7", "europa.jpg"),
        new(new("sol/ganymede"), "Ganymede", CatalogueKind.Moon, new("sol/jupiter"), 503, 2634.1, "c3b5a2", "ganymede.jpg"),
        new(new("sol/callisto"), "Callisto", CatalogueKind.Moon, new("sol/jupiter"), 504, 2410.3, "a99484", "callisto.jpg"),
        new(new("sol/titan"), "Titan", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 606, 2574.73, "dca04a", "titan.jpg"),
        new(PlutoId, "Pluto", CatalogueKind.DwarfPlanet, SolarSystemSample.SunId, 999, 1188.3, "d9b6a0", "pluto.jpg"),
        new(new("sol/ceres"), "Ceres", CatalogueKind.DwarfPlanet, SolarSystemSample.SunId, 2000001, 469.7, "b5b0a6", "ceres.jpg", "ceres.xyzv"),
        new(new("sol/vesta"), "Vesta", CatalogueKind.Asteroid, SolarSystemSample.SunId, 2000004, 262.7, "b9ac99", "vesta.jpg", "vesta.xyzv"),
        new(CometId, "67P/Churyumov–Gerasimenko", CatalogueKind.Comet, SolarSystemSample.SunId, 1000012, 2, "827065", null, "67p.xyzv")
    });

    private static readonly Dictionary<ObjectId, NaturalBodyDefinition> ById = Bodies.ToDictionary(body => body.Id);

    public static NaturalBodyDefinition? Find(ObjectId id) => ById.GetValueOrDefault(id);
}
