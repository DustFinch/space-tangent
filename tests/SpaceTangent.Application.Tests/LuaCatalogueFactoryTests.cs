// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LuaCatalogueFactoryTests
{
    [OriginalFactoriesFact]
    [Trait("Category", "ExternalData")]
    public void RealSourceFactoriesMatchArchivedPositionVelocityAndOrientation()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_SSC")!;
        var diagnostics = new List<string>();
        var bodies = SscBodyCatalogue.Load(root, scripts: new LuaCatalogueFactories(), warning: diagnostics.Add);
        Assert.Equal(3, diagnostics.Count);
        Assert.Equal(["Fallback Sample", "Fallback Spin", "Cassini SOI"], bodies.Select(body => body.Name));
        var craft = Assert.Single(bodies, body => body.Class == "spacecraft");
        using var reference = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_REFERENCE")!));
        foreach (var row in reference.RootElement.GetProperty("rows").EnumerateArray())
        {
            double day = row.GetProperty("day").GetDouble();
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            double[] state = row.GetProperty("state").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            double[] spin = row.GetProperty("spinWxyz").EnumerateArray().Select(item => item.GetDouble()).ToArray();
            var actual = Assert.IsType<CartesianState>(craft.Sample(epoch));
            Assert.InRange((actual.PositionKilometers - EclipticToIcrf(state, 0)).Length, 0, 2e-7);
            Assert.InRange((actual.VelocityKilometersPerSecond - EclipticToIcrf(state, 3) / 86_400).Length, 0, 1e-9);
            var x = craft.SampleRotation!(epoch)!.Value.ToWorld.X;
            var referenceX = new Vector3d(1 - 2 * spin[2] * spin[2], -2 * spin[0] * spin[2], 0);
            Assert.InRange((x - referenceX).Length, 0, 2e-9);
        }
    }

    [OriginalFactoriesFact]
    [Trait("Category", "ExternalData")]
    public void NestedModulesAndUtf8AddonPathArePartOfTheSourceGeneration()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_MODULES")!);
        var (bodies, inputs) = AddonDependencies.Capture(() => SscBodyCatalogue.Load(root, scripts: new LuaCatalogueFactories()));
        var craft = Assert.Single(bodies, body => body.Class == "spacecraft");
        var sample = Assert.IsType<CartesianState>(craft.Sample(JulianInstant.FromJulianDate(2453187.55, TimeScale.Tdb)));
        Assert.Equal(160_000 + Encoding.UTF8.GetByteCount(root), sample.PositionKilometers.X, 6);
        Assert.True(inputs.IsCurrent());
    }

    [ArchivedFactoryPackFact]
    [Trait("Category", "ExternalData")]
    public void RelocatedVersion17PackResolvesItsOwnModulesAndMatchesNativeCassiniStates()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_ARCHIVED_PACK")!;
        string referencePath = Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_ARCHIVED_REFERENCE")!;
        var temporary = Directory.CreateTempSubdirectory("st-stage49-lua-context-");
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(temporary.FullName, "00-modules")).FullName;
            foreach (string source in Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, Path.GetRelativePath(archive, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "pack.json")));
            Assert.Equal(17, manifest.RootElement.GetProperty("formatVersion").GetInt32());
            var diagnostics = new List<string>();
            var (bodies, inputs) = AddonDependencies.Capture(() =>
                SscBodyCatalogue.Load(root, scripts: new LuaCatalogueFactories(), warning: diagnostics.Add));
            Assert.Equal(["Fallback Sample", "Fallback Spin", "Cassini SOI"], bodies.Select(body => body.Name));
            Assert.Equal(3, diagnostics.Count);
            Assert.All(diagnostics, message => Assert.Contains("initialization failed", message));
            var craft = Assert.Single(bodies, body => body.Class == "spacecraft");
            using var reference = JsonDocument.Parse(File.ReadAllText(referencePath));
            var rows = reference.RootElement.GetProperty("rows");
            // The legacy fixture deliberately adds the UTF-8 byte length of AddonPath to its X position.
            double offset = 160_000 + Encoding.UTF8.GetByteCount(root) - rows[0].GetProperty("state")[0].GetDouble();
            foreach (var row in rows.EnumerateArray())
            {
                var epoch = JulianInstant.FromJulianDate(row.GetProperty("day").GetDouble(), TimeScale.Tdb);
                var state = row.GetProperty("state").EnumerateArray().Select(item => item.GetDouble()).ToArray();
                state[0] += offset;
                var actual = Assert.IsType<CartesianState>(craft.Sample(epoch));
                Assert.InRange((actual.PositionKilometers - EclipticToIcrf(state, 0)).Length, 0, 2e-7);
                Assert.InRange((actual.VelocityKilometersPerSecond - EclipticToIcrf(state, 3) / 86_400).Length, 0, 1e-9);
                var spin = row.GetProperty("spinWxyz").EnumerateArray().Select(item => item.GetDouble()).ToArray();
                var expectedX = new Vector3d(1 - 2 * spin[2] * spin[2], -2 * spin[0] * spin[2], 0);
                Assert.InRange((craft.SampleRotation!(epoch)!.Value.ToWorld.X - expectedX).Length, 0, 2e-9);
            }

            Assert.True(inputs.IsCurrent());
            File.AppendAllText(Path.Combine(root, "scripts", "pkg", "curve.lua"), "\n-- changed module");
            Assert.False(inputs.IsCurrent());
        }
        finally { temporary.Delete(recursive: true); }
    }

    private sealed class ArchivedFactoryPackFactAttribute : FactAttribute
    {
        public ArchivedFactoryPackFactAttribute()
        {
            if (!Directory.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_ARCHIVED_PACK")) ||
                !File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_SCRIPTED_ARCHIVED_REFERENCE")))
                Skip = "Set the archived v17 Cassini factory pack and native reference.";
        }
    }


    private static Vector3d EclipticToIcrf(double[] source, int offset)
    {
        var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
        double x = source[offset], y = -source[offset + 2], z = source[offset + 1];
        return new(x, cos * y - sin * z, sin * y + cos * z);
    }

    private sealed class OriginalFactoriesFactAttribute : FactAttribute
    {
        public OriginalFactoriesFactAttribute()
        {
            if (new[] { "SPACETANGENT_SCRIPTED_SSC", "SPACETANGENT_SCRIPTED_REFERENCE", "SPACETANGENT_SCRIPTED_MODULES" }
                .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
                Skip = "Set the archived scripted Cassini sources and native reference.";
        }
    }
}
