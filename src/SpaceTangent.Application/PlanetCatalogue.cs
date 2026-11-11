// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

public sealed record PlanetDefinition(ObjectId Id, ObjectId SystemId, string Name, int NaifId,
    double RadiusKilometers, double PolarRadiusKilometers, double PeriodDays, string Color, string Texture);

public static class PlanetCatalogue
{
    public static ObjectId MarsId { get; } = new("sol/mars");
    public static ObjectId SaturnId { get; } = new("sol/saturn");
    public static IReadOnlyList<PlanetDefinition> Planets { get; } = Array.AsReadOnly(new PlanetDefinition[]
    {
        new(SolarSystemSample.MercuryId, SolarSystemSample.MercuryId, "Mercury", 199, 2439.7, 2439.7, 87.97, "aa9a88", "mercury.jpg"),
        new(SolarSystemSample.VenusId, SolarSystemSample.VenusId, "Venus", 299, 6051.8, 6051.8, 224.70, "e1b586", "venus-clouds.jpg"),
        new(SolarSystemSample.EarthId, SolarSystemSample.EarthId, "Earth", 399, 6378.137, 6356.752, 365.26, "6fa8ff", "earth.jpg"),
        new(MarsId, SolarSystemSample.MarsBarycenterId, "Mars", 499, 3396.19, 3376.20, 686.98, "d08461", "mars.jpg"),
        new(new("sol/jupiter"), SolarSystemSample.JupiterBarycenterId, "Jupiter", 599, 71492, 66854, 4332.59, "d4b295", "jupiter.jpg"),
        new(SaturnId, SolarSystemSample.SaturnBarycenterId, "Saturn", 699, 60268, 54364, 10759.22, "d9bb7f", "saturn.jpg"),
        new(new("sol/uranus"), SolarSystemSample.UranusBarycenterId, "Uranus", 799, 25559, 24973, 30688.5, "a2dadd", "uranus.jpg"),
        new(new("sol/neptune"), SolarSystemSample.NeptuneBarycenterId, "Neptune", 899, 24764, 24341, 60182, "7595e3", "neptune.jpg")
    });

    private static readonly Dictionary<ObjectId, PlanetDefinition> ById = Planets.ToDictionary(planet => planet.Id);

    public static PlanetDefinition? Find(ObjectId id) => ById.GetValueOrDefault(id);
}
