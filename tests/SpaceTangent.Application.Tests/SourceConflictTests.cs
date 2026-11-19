// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SourceConflictTests
{
    [Fact]
    public void FirstMissionDefinitionKeepsItsTrajectoryAndReportsBothSources()
    {
        var first = Mission(new("mission/example"), "first.glb", 100);
        var second = Mission(first.Id, "second.glb", 200);
        var world = new SimulationWorld(new SharedClockTests.StaticEphemeris(), [], sampledMissions: [first, second]);
        Assert.Same(first, world.FindSampledMission(first.Id));
        Assert.True(world.Graph.Evaluate(first.Start).TryState(first.Id, out var state));
        Assert.Equal(100, state.PositionKilometers.X);
        string warning = Assert.Single(world.SourceWarnings);
        Assert.Contains("first.glb", warning);
        Assert.Contains("second.glb", warning);
    }

    [Fact]
    public void UnsupportedCrossKindCollisionFailsBeforeInstallingTheMission()
    {
        var mission = Mission(SolarSystemSample.EarthId, "earth.glb", 100);
        var error = Assert.Throws<InvalidDataException>(() =>
            new SimulationWorld(new SharedClockTests.StaticEphemeris(), [], sampledMissions: [mission]));
        Assert.Contains("conflicts", error.Message);
        Assert.Contains("earth.glb", error.Message);
    }

    [ScienceFact]
    [Trait("Category", "ExternalData")]
    public void DuplicateOriginalJasonKeepsOneConsistentScienceTrack()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        var first = ScienceSatelliteCatalog.Load(Path.Combine(root, "catalog/sciencesat.json"), root)[0];
        var duplicate = ScienceSatelliteCatalog.Load(Path.Combine(root, "catalog/sciencesat.json"), root)[0];
        var world = new SimulationWorld(new SharedClockTests.StaticEphemeris(), [first, duplicate]);
        Assert.Same(first, Assert.Single(world.Satellites));
        Assert.Equal(first.Name, world.Catalogue.Find(first.Id)!.Name);
        Assert.Single(world.SourceWarnings);
        Assert.True(world.Graph.Evaluate(first.Epoch).TryState(first.Id, out _));
    }

    private static SampledMission Mission(ObjectId id, string source, double x)
    {
        var ephemeris = new SharedClockTests.StaticEphemeris();
        var frame = new MissionFrame.Inertial(Basis3d.Identity);
        return new(id, id.Value, source, 1,
            [new(ephemeris.Start, ephemeris.End, SolarSystemSample.EarthId,
                _ => new CartesianState(new(x, 0, 0), default), frame, frame)]);
    }

    private sealed class ScienceFactAttribute : FactAttribute
    {
        public ScienceFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set the original science catalogue root.";
        }
    }
}
