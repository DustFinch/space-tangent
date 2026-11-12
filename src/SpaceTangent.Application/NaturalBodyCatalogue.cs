// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

public sealed record NaturalBodyDefinition(ObjectId Id, string Name, CatalogueKind Kind, ObjectId Parent,
    int NaifId, double RadiusKilometers, string Color, string? Texture, string? SampledOrbit = null, SatelliteTheory? Theory = null);

public static class NaturalBodyCatalogue
{
    public static ObjectId CometId { get; } = new("sol/67p");
    internal const int HyperionNaifId = 607;
    public static ObjectId PlutoId { get; } = new("sol/pluto");

    public static IReadOnlyList<NaturalBodyDefinition> Bodies { get; } = Array.AsReadOnly(new NaturalBodyDefinition[]
    {
        new(new("sol/phobos"), "Phobos", CatalogueKind.Moon, PlanetCatalogue.MarsId, 401, 11.1, "a69580", "phobos.jpg"),
        new(new("sol/deimos"), "Deimos", CatalogueKind.Moon, PlanetCatalogue.MarsId, 402, 6.2, "bca38b", "deimos.jpg"),
        new(new("sol/io"), "Io", CatalogueKind.Moon, new("sol/jupiter"), 501, 1821.6, "f1d385", "io.jpg", Theory: SatelliteTheory.Io),
        new(new("sol/europa"), "Europa", CatalogueKind.Moon, new("sol/jupiter"), 502, 1560.8, "dbc7a7", "europa.jpg", Theory: SatelliteTheory.Europa),
        new(new("sol/ganymede"), "Ganymede", CatalogueKind.Moon, new("sol/jupiter"), 503, 2634.1, "c3b5a2", "ganymede.jpg", Theory: SatelliteTheory.Ganymede),
        new(new("sol/callisto"), "Callisto", CatalogueKind.Moon, new("sol/jupiter"), 504, 2410.3, "a99484", "callisto.jpg", Theory: SatelliteTheory.Callisto),
        new(new("sol/titan"), "Titan", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 606, 2574.73, "dca04a", "titan.jpg", Theory: SatelliteTheory.Titan),
        new(new("sol/mimas"), "Mimas", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 601, 206.6, "cac7bf", "mimas.jpg", Theory: SatelliteTheory.Mimas),
        new(new("sol/enceladus"), "Enceladus", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 602, 256.2, "e1e4e5", "enceladus.jpg", Theory: SatelliteTheory.Enceladus),
        new(new("sol/tethys"), "Tethys", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 603, 538.4, "cdcac4", "tethys.jpg", Theory: SatelliteTheory.Tethys),
        new(new("sol/dione"), "Dione", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 604, 563.4, "bfbdb7", "dione.jpg", Theory: SatelliteTheory.Dione),
        new(new("sol/rhea"), "Rhea", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 605, 765.68, "bfbcb8", "rhea.jpg", Theory: SatelliteTheory.Rhea),
        new(new("sol/hyperion"), "Hyperion", CatalogueKind.Moon, PlanetCatalogue.SaturnId, HyperionNaifId, 180.9, "b8a592", "hyperion.jpg", Theory: SatelliteTheory.Hyperion),
        new(new("sol/iapetus"), "Iapetus", CatalogueKind.Moon, PlanetCatalogue.SaturnId, 608, 745.7, "a89b8d", "iapetus.jpg", Theory: SatelliteTheory.Iapetus),
        new(new("sol/miranda"), "Miranda", CatalogueKind.Moon, new("sol/uranus"), 705, 240.4, "a9aaad", "miranda.jpg", Theory: SatelliteTheory.Miranda),
        new(new("sol/ariel"), "Ariel", CatalogueKind.Moon, new("sol/uranus"), 701, 581.1, "b3b1ae", "ariel.jpg", Theory: SatelliteTheory.Ariel),
        new(new("sol/umbriel"), "Umbriel", CatalogueKind.Moon, new("sol/uranus"), 702, 582.4, "a8a7a4", "umbriel.jpg", Theory: SatelliteTheory.Umbriel),
        new(new("sol/titania"), "Titania", CatalogueKind.Moon, new("sol/uranus"), 703, 788.4, "b8b3b0", "titania.jpg", Theory: SatelliteTheory.Titania),
        new(new("sol/oberon"), "Oberon", CatalogueKind.Moon, new("sol/uranus"), 704, 761.4, "b6b3ae", "oberon.jpg", Theory: SatelliteTheory.Oberon),
        new(new("sol/triton"), "Triton", CatalogueKind.Moon, new("sol/neptune"), 801, 1354.6, "eadfcb", "triton.jpg", Theory: SatelliteTheory.Triton),
        new(PlutoId, "Pluto", CatalogueKind.DwarfPlanet, SolarSystemSample.SunId, 999, 1188.3, "d9b6a0", "pluto.jpg"),
        new(new("sol/ceres"), "Ceres", CatalogueKind.DwarfPlanet, SolarSystemSample.SunId, 2000001, 469.7, "b5b0a6", "ceres.jpg", "ceres.xyzv"),
        new(new("sol/vesta"), "Vesta", CatalogueKind.Asteroid, SolarSystemSample.SunId, 2000004, 262.7, "b9ac99", "vesta.jpg", "vesta.xyzv"),
        new(CometId, "67P/Churyumov–Gerasimenko", CatalogueKind.Comet, SolarSystemSample.SunId, 1000012, 2, "827065", null, "67p.xyzv")
    });

    private static readonly Dictionary<ObjectId, NaturalBodyDefinition> ById = Bodies.ToDictionary(body => body.Id);

    public static NaturalBodyDefinition? Find(ObjectId id) => ById.GetValueOrDefault(id);
}
