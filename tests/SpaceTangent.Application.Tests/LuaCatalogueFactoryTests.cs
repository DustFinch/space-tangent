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
