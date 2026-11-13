// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ExtrasolarSscTests
{
    [ArchivedPlanetHostFact]
    [Trait("Category", "ExternalData")]
    public void OriginalKeplerPlanetUsesThePositionedComponentAndKeepsItsOldLink()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var stars = StellarCatalogue.LoadArchive(Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")!)
            .WithStc(Environment.GetEnvironmentVariable("SPACETANGENT_STC_ROOT")!).Catalogue;
        var planets = ExoplanetCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_EXOPLANET_CATALOGUE")!);
        var world = new SimulationWorld(ephemeris, [], stellar: stars, exoplanets: planets);
        var planet = planets.Find(new("exo/4294958019/kepler-931-ab"))!;
        var component = stars.FindByName("Kepler-931 A")!;
        var snapshot = world.Graph.Evaluate(JulianInstant.FromJulianDate(2460600.5, TimeScale.Tdb));
        Assert.Equal(component.Id, world.Catalogue.Find(planet.Id)!.Parent);
        Assert.Equal(component.Id, world.FindStellarTarget(planet.Host)!.Id);
        Assert.True(world.TryNavigationState(component.Id, snapshot, out var primary));
        Assert.True(world.TryNavigationState(planet.Host, snapshot, out var legacy));
        Assert.Equal(primary, legacy);
        Assert.True(world.TryNavigationState(planet.Id, snapshot, out var observed));
        Assert.InRange(((observed - primary).PositionKilometers -
            planet.ParentRelativeState(snapshot.Epoch).PositionKilometers).Length, 0, 3);
    }

    private sealed class ArchivedPlanetHostFactAttribute : FactAttribute
    {
        public ArchivedPlanetHostFactAttribute()
        {
            if (new[] { "SPACETANGENT_DE440", "SPACETANGENT_STELLAR_ARCHIVE", "SPACETANGENT_STC_ROOT",
                    "SPACETANGENT_EXOPLANET_CATALOGUE" }.Any(key => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the archived stellar and exoplanet inputs and DE440.";
        }
    }

    [ExternalSscFact]
    [Trait("Category", "ExternalData")]
    public void LegacySiriusSystemKeepsStarAndMoonOrbitUnits()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var stars = StellarCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_STARS")!);
        var root = Directory.CreateTempSubdirectory("st-extrasolar-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "system.ssc"), """
                "Test Moon B" "Sirius/Test Giant" { Radius 1500 EllipticalOrbit { Period 3 SemiMajorAxis 400000 } }
                "Test Giant" "Sirius" { Radius 70000 EllipticalOrbit { Period 4 SemiMajorAxis 5 } }
                """);
            var world = new SimulationWorld(ephemeris, [], stellar: stars, bodyCatalog: SscBodyCatalogue.Load(root.FullName));
            var star = new ObjectId("hip/32349");
            var planet = new ObjectId("ssc/sirius/test-giant");
            var moon = new ObjectId("ssc/sirius/test-giant/test-moon-b");
            var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb);
            var snapshot = world.Graph.Evaluate(epoch);
            Assert.Equal(CatalogueKind.Planet, world.Catalogue.Find(planet)!.Kind);
            Assert.Equal(CatalogueKind.Moon, world.Catalogue.Find(moon)!.Kind);
            Assert.Equal(star, world.Catalogue.Find(planet)!.Parent);
            Assert.Equal(planet, world.Catalogue.Find(moon)!.Parent);
            Assert.InRange(Separation(snapshot, planet, star), 747_989_353, 747_989_354);
            Assert.InRange(Separation(snapshot, moon, planet), 399_999.98, 400_000.02);
            var lunarOrbit = world.SscBodies.Single(body => body.Id == moon).Orbit!.Value;
            Assert.Equal(planet, lunarOrbit.Center);
            Assert.Equal(121, lunarOrbit.Points.Count);
            Assert.Equal(lunarOrbit.Points[0], lunarOrbit.Points[^1]);
            Assert.All(lunarOrbit.Points, point => Assert.InRange(point.Length, 399_999.98, 400_000.02));
            var later = world.Graph.Evaluate(epoch.AddSeconds(86_400));
            Assert.InRange(Separation(later, moon, planet), 399_999.98, 400_000.02);
            Assert.True(later.TryState(moon, out var next) && snapshot.TryState(moon, out var initial) &&
                (next - initial).PositionKilometers.Length > 100_000);
        }
        finally { root.Delete(recursive: true); }
    }

    private static double Separation(WorldSnapshot snapshot, ObjectId target, ObjectId center)
    {
        Assert.True(snapshot.TryState(target, out var state));
        Assert.True(snapshot.TryState(center, out var parent));
        return (state - parent).PositionKilometers.Length;
    }

    private sealed class ExternalSscFactAttribute : FactAttribute
    {
        public ExternalSscFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_STARS")))
                Skip = "Set the verified DE440 and spatial star catalogue.";
        }
    }
}
