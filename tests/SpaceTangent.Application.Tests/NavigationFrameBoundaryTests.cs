// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NavigationFrameBoundaryTests
{
    private static readonly ObjectId Parent = new("parent"), Probe = new("probe");
    private static readonly JulianInstant Epoch = new(2451545, 0, TimeScale.Tdb);
    private static WorldSnapshot Snapshot(Basis3d reference)
    {
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(Parent, _ => new(new(3, 4, 5), default));
        var axes = new MissionFrame.Inertial(Basis3d.Identity);
        graph.AddSampledMission(new(Probe, "Witness", null, 1,
            [new(Epoch, Epoch.AddSeconds(100), Parent, _ => new(new(10, 20, 30), default), axes, axes)
                { BodyReferenceFrame = new MissionFrame.Inertial(reference) }]));
        return graph.Build().Evaluate(Epoch);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CatalogueReferencesNormalizeTheirAxesAndChooseTheCorrectOrigin(bool orbit)
    {
        var snapshot = Snapshot(new(new(2, 0, 0), new(0, 3, 0), new(0, 0, 4)));
        Assert.True(NavigationCamera.TryCatalogueReference(snapshot, Probe, new(Epoch, orbit), out var origin, out var basis));
        Assert.Equal(orbit ? new Vector3d(3, 4, 5) : new Vector3d(13, 24, 35), origin);
        Assert.Equal(Basis3d.Identity, basis);
    }

    [Theory]
    [InlineData("zero-pole")]
    [InlineData("parallel")]
    [InlineData("nonfinite-pole")]
    [InlineData("nonfinite-x")]
    public void DegenerateCatalogueReferencesAreRejectedWithoutPublishingABasis(string fault)
    {
        var x = fault == "parallel" ? new Vector3d(0, 0, 1) : fault == "nonfinite-x" ? new(double.NaN, 0, 0) : new(1, 0, 0);
        var z = fault == "zero-pole" ? default : fault == "nonfinite-pole" ? new Vector3d(0, 0, double.PositiveInfinity) : new(0, 0, 1);
        Assert.False(NavigationCamera.TryCatalogueReference(Snapshot(new(x, new(0, 1, 0), z)), Probe,
            new(Epoch, false), out _, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EquatorialFrameRetainsThePoleAndUsesAStableAscendingNode(bool tilted)
    {
        double sin = tilted ? Math.Sqrt(3) / 2 : 0, cos = tilted ? .5 : 1;
        var graph = new WorldGraphBuilder(); graph.AddOrientation(Probe, _ => new(new(new(cos, 0, -sin), new(0, 1, 0), new(sin, 0, cos)), default(Vector3d)));
        Assert.True(NavigationCamera.TryMeanEquator(graph.Build().Evaluate(Epoch), Probe, out var basis));
        Assert.Equal(new Vector3d(sin, 0, cos), basis.Z);
        Assert.Equal(tilted ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0), basis.X);
        Assert.InRange((Vector3d.Cross(basis.X, basis.Y) - basis.Z).Length, 0, 1e-12);
    }

    [Fact]
    public void MissingReferenceFramesFallBackToTheSavedOrbitalPose()
    {
        var snapshot = new WorldGraphBuilder().Build().Evaluate(Epoch);
        var camera = new NavigationCamera(default, 100, Math.PI / 2, 0, Probe, true)
            { Placement = new(new(0, 100, 0), new(0, -1, 0), new(0, 0, 1)) { Frame = NavigationFrame.Equatorial } };
        var pose = camera.ResolvePlacement(snapshot, DeltaTModel.FixedOffset);
        Assert.InRange((pose.OffsetKilometers - new Vector3d(100, 0, 0)).Length, 0, 1e-12);
        Assert.False(NavigationCamera.TryMeanEquator(snapshot, Probe, out _));
        Assert.False(NavigationCamera.TryMeanEquator(snapshot, SolarSystemSample.EarthId, out _));
        Assert.False(NavigationCamera.TryCatalogueReference(snapshot, Probe, new(Epoch, false), out _, out _));
    }

    [Theory]
    [InlineData(NavigationFrame.Inertial)]
    [InlineData(NavigationFrame.Chase)]
    [InlineData(NavigationFrame.PhaseLock)]
    [InlineData(NavigationFrame.Surface)]
    public void RelativeFramesCannotUseMissingOrZeroLengthDirections(NavigationFrame frame)
    {
        var graph = new WorldGraphBuilder(); graph.AddTrajectory(Probe, _ => default(CartesianState));
        graph.AddTrajectory(Parent, _ => default(CartesianState));
        Assert.False(NavigationCamera.TryRelativeBasis(graph.Build().Evaluate(Epoch), frame, Probe, Parent, out _));
    }

    [Fact]
    public void InvalidEarthTimeModelDoesNotPublishABodyFrame()
    {
        var graph = new WorldGraphBuilder(); graph.AddSolarSystem(new SharedClockTests.StaticEphemeris());
        Assert.False(NavigationCamera.TryBodyBasis(graph.Build().Evaluate(Epoch), SolarSystemSample.EarthId,
            (DeltaTModel)999, out _));
    }
}
