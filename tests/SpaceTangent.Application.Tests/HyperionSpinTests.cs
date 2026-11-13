// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class HyperionSpinTests
{
    [LegacySpinFact]
    [Trait("Category", "ExternalData")]
    public void HyperionUsesTheArchivedPrecessingOrientation()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var world = new SimulationWorld(ephemeris, []);
        using var oracle = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("SPACETANGENT_HYPERION_SPIN")!));
        var id = new ObjectId("sol/hyperion");
        foreach (var row in oracle.RootElement.EnumerateArray())
        {
            var epoch = JulianInstant.FromJulianDate(row[0].GetDouble(), TimeScale.Tdb);
            Assert.True(world.Graph.Evaluate(epoch).TryOrientation(id, out var frame));
            Check(frame.ToWorld.X, 1);
            Check(frame.ToWorld.Y, 4);
            Check(frame.ToWorld.Z, 7);
            Assert.NotNull(frame.AngularVelocityRadiansPerSecond);
            Assert.True(world.Graph.Evaluate(epoch.AddSeconds(-10)).TryOrientation(id, out var before));
            Assert.True(world.Graph.Evaluate(epoch.AddSeconds(10)).TryOrientation(id, out var after));
            var estimated = (Vector3d.Cross(frame.ToWorld.X, (after.ToWorld.X - before.ToWorld.X) / 20) +
                Vector3d.Cross(frame.ToWorld.Y, (after.ToWorld.Y - before.ToWorld.Y) / 20) +
                Vector3d.Cross(frame.ToWorld.Z, (after.ToWorld.Z - before.ToWorld.Z) / 20)) * .5;
            Assert.InRange((estimated - frame.AngularVelocityRadiansPerSecond!.Value).Length, 0, 1e-12);

            void Check(Vector3d actual, int index)
            {
                var expected = new Vector3d(row[index].GetDouble(), row[index + 1].GetDouble(), row[index + 2].GetDouble());
                Assert.InRange((actual - expected).Length, 0, 2e-9);
            }
        }
        Assert.Equal(8, oracle.RootElement.GetArrayLength());
    }

    private sealed class LegacySpinFactAttribute : FactAttribute
    {
        public LegacySpinFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_HYPERION_SPIN")) ||
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the archived Hyperion spin reference and DE440 paths.";
        }
    }
}
