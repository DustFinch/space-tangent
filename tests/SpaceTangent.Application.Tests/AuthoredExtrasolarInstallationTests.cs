// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class AuthoredExtrasolarInstallationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-extrasolar-install-");
    // Authored Cartesian witness, deliberately not a scientific position for Sirius.
    internal static StellarCatalogue Stars() => StellarCatalogue.Read(new StringReader(
        "id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n" +
        "hip/32349\tSirius\t2\t2\t0\t0\t0\t0\t0\t-1\t0\t1000000\n"));

    private SimulationWorld Load(string text, StellarCatalogue? stars = null, ExoplanetCatalogue? planets = null)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "witness.ssc"), text);
        return new(new SharedClockTests.StaticEphemeris(), [], stellar: stars ?? Stars(),
            exoplanets: planets, bodyCatalog: SscBodyCatalogue.Load(_root.FullName));
    }

    [Theory]
    [InlineData("spacecraft", CatalogueKind.Spacecraft)]
    [InlineData("planet", CatalogueKind.Planet)]
    [InlineData("moon", CatalogueKind.Moon)]
    [InlineData("minormoon", CatalogueKind.Moon)]
    [InlineData("comet", CatalogueKind.Comet)]
    [InlineData("dwarfplanet", CatalogueKind.DwarfPlanet)]
    [InlineData("dwarf planet", CatalogueKind.DwarfPlanet)]
    [InlineData("unknown", CatalogueKind.Asteroid)]
    public void CatalogueClassificationPreservesImportedBodyClasses(string kind, CatalogueKind expected)
    {
        var body = new SscBodyDefinition("Witness", "Sol", "Sol", kind, 1, default, null, default,
            Basis3d.Identity, null, _ => new CartesianState(new(1, 0, 0), default));
        Assert.Equal(expected, new InstalledSscBody(new("witness"), new("sol"), body).Kind);
    }

    [Fact]
    public void StellarAliasPathsInstallNestedOrbitsOnceAndRetainPhysicalUnits()
    {
        var world = Load("""
            "Giant:Alias" "Sirius" { Radius 100 EllipticalOrbit { Period 2 SemiMajorAxis 3 } }
            "Moon" "Sirius/Alias" { Radius 2 EllipticalOrbit { Period 2 SemiMajorAxis 300 } }
            "Sibling" "Sirius" { Radius 3 FixedPosition [0.01 0 0] }
            """);
        var giant = world.SscBodies.Single(body => body.Definition.Name == "Giant");
        var moon = world.SscBodies.Single(body => body.Definition.Name == "Moon");
        Assert.Equal(giant.Id, moon.Parent);
        Assert.Equal(new ObjectId("hip/32349"), giant.StellarHost);
        Assert.Equal(giant.StellarHost, moon.StellarHost);
        Assert.Equal(CatalogueKind.Planet, giant.Kind);
        Assert.Equal(CatalogueKind.Moon, moon.Kind);
        var snapshot = world.Graph.Evaluate(new(2451545, 0, TimeScale.Tdb));
        Assert.True(snapshot.TryState(giant.Id, out var parent));
        Assert.True(snapshot.TryState(moon.Id, out var child));
        Assert.InRange((child - parent).PositionKilometers.Length, 299.999999, 300.000001);
        Assert.Equal(3, world.SscBodies.Count);
    }

    [Theory]
    [InlineData("collision")]
    [InlineData("empty-slug")]
    [InlineData("missing-parent")]
    [InlineData("too-many")]
    public void InvalidInstallationsFailBeforePublishingAWorld(string fault)
    {
        SscBodyDefinition Body(string name, string parent = "Sol") => new(name, parent, parent, "asteroid", 1,
            default, null, default, Basis3d.Identity, null, _ => new CartesianState(new(1, 0, 0), default));
        SscBodyDefinition[] definitions = fault switch
        {
            "collision" => [Body("Witness:Alias"), Body("Other:Alias")],
            "empty-slug" => [Body("😀")],
            "missing-parent" => [Body("Witness", "Absent")],
            _ => Enumerable.Range(0, 513).Select(i => Body("Witness" + i)).ToArray()
        };
        Assert.Throws<InvalidDataException>(() => new SimulationWorld(new SharedClockTests.StaticEphemeris(), [], bodyCatalog: definitions));
    }

    [Fact]
    public void DuplicateNormalizedIdentifiersDoNotBypassThePathCheck()
    {
        Assert.Throws<InvalidDataException>(() => Load("""
            "First.Name" "Sol" { FixedPosition [1 0 0] }
            "First Name" "Sol" { FixedPosition [2 0 0] }
            """));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ImportedMoonResolvesExoplanetAliasesWithEitherEmbeddedOrMeasuredHost(bool measuredHost)
    {
        string catalogue = Path.Combine(_root.FullName, "planets.txt");
        File.WriteAllText(catalogue,
            "b:SecondaryB\t32349\tSirius\t3\t0\t0\t4\t5772\t6000\trocky\t1\t0.3\t10\t14959787.07\t0\t0\t0\t0\t0\t2451545\t24\t0\t0\t0\t2451545\n");
        var planets = ExoplanetCatalogue.Load(catalogue);
        var world = Load("""
            "WitnessMoon" "Sirius/SecondaryB" { Radius 100 EllipticalOrbit { Period 2 SemiMajorAxis 400000 } }
            "SiblingMoon" "Sirius/b" { Radius 80 EllipticalOrbit { Period 3 SemiMajorAxis 500000 } }
            """, measuredHost ? Stars() : StellarCatalogue.Empty, planets);
        var moon = world.SscBodies.Single(body => body.Definition.Name == "WitnessMoon");
        var companion = world.SscBodies.Single(body => body.Definition.Name == "SiblingMoon");
        Assert.Equal(new ObjectId("exo/32349/b"), moon.Parent);
        Assert.Equal(moon.Parent, companion.Parent);
        Assert.Equal(new ObjectId("hip/32349"), moon.StellarHost);
        Assert.Equal(CatalogueKind.Moon, moon.Kind);
        foreach (int seconds in new[] { 0, 3600, 86400 })
        {
            var snapshot = world.Graph.Evaluate(new JulianInstant(2451545, 0, TimeScale.Tdb).AddSeconds(seconds));
            Assert.True(snapshot.TryState(moon.Parent, out var parent));
            Assert.True(snapshot.TryState(moon.Id, out var child));
            Assert.InRange((child - parent).PositionKilometers.Length, 399999.99, 400000.01);
            Assert.True(snapshot.TryState(new("hip/32349"), out var host));
            Assert.InRange((parent - host).PositionKilometers.Length, 14959787, 14959788);
        }
    }

    [Theory]
    [InlineData("Ceres")] [InlineData("1 Ceres")]
    public void NumberedAndPlainMinorPlanetNamesReuseTheBuiltInIdentifier(string name)
    {
        var world = Load($"\"{name}\" \"Sol\" {{ Radius 460 FixedPosition [1 2 3] }}");
        var installed = Assert.Single(world.SscBodies);
        Assert.Equal("sol/ceres", installed.Id.Value);
        Assert.True(world.Graph.Evaluate(new(2451545, 0, TimeScale.Tdb)).TryState(installed.Id, out var state));
        Assert.InRange(Math.Abs((state.PositionKilometers - world.StellarOrigin).Length - Math.Sqrt(14)), 0, 1e-7);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
