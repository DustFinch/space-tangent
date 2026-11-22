// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SceneLinkFrameContractTests
{
    private static readonly JulianInstant Epoch = new(2451545, 0, TimeScale.Tdb);
    private sealed class MovingEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => Epoch.AddSeconds(-86400);
        public JulianInstant End => Epoch.AddSeconds(86400);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch, default,
            new(new(149_597_870.7, 0, 0), new(0, 30, 0)),
            new(new(149_982_270.7, 0, 0), new(0, 31.023, 0)), default);
    }
    private static readonly SimulationWorld World = new(new MovingEphemeris(), []);
    private const DeltaTModel Delta = DeltaTModel.MorrisonStephenson2004;

    [Theory]
    [InlineData("follow")]
    [InlineData("free")]
    [InlineData("body-fixed")]
    [InlineData("surface")]
    [InlineData("chase")]
    [InlineData("phase-lock")]
    [InlineData("equatorial")]
    public void LegacyLinkRoundTripsWorldCameraPoseAcrossReferenceFrames(string frame)
    {
        var snapshot = World.Graph.Evaluate(Epoch, Delta);
        Assert.True(snapshot.TryState(SolarSystemSample.EarthId, out var earth));
        var placement = new NavigationPlacement(new(12000, 0, 0), new(0, 1, 0), new(0, 0, 1))
        {
            BodyFixed = frame == "body-fixed",
            Frame = frame switch
            { "surface" => NavigationFrame.Surface, "chase" => NavigationFrame.Chase,
                "phase-lock" => NavigationFrame.PhaseLock, "equatorial" => NavigationFrame.Equatorial,
                _ => NavigationFrame.Inertial },
            Reference = frame == "surface" ? SolarSystemSample.EarthId :
                frame is "chase" or "phase-lock" ? SolarSystemSample.SunId : null
        };
        var camera = new NavigationCamera(earth.PositionKilometers, 12000, 0, 0,
            frame == "free" ? null : frame == "surface" ? SolarSystemSample.MoonId : SolarSystemSample.EarthId,
            frame != "free") { Placement = placement };
        Assert.True(camera.IsValid());
        var original = new ObserverView(Epoch, CameraMode.Solar, SolarSystemSample.MoonId, false)
            { Navigation = camera, FieldOfViewDegrees = 33, PlaybackRate = -120 };
        string link = SceneLink.WriteLegacy(original, World, Delta);
        var parsed = SceneLink.Read(link, World, Delta);
        Assert.Null(parsed.Warning);
        var restored = Assert.Single(parsed.Scenario.Views);
        var actual = Assert.IsType<NavigationCamera>(restored.Navigation);
        var before = camera.ResolvePlacement(snapshot, Delta);
        var after = actual.ResolvePlacement(snapshot, Delta);
        Assert.InRange((actual.AnchorKilometers + after.OffsetKilometers - camera.AnchorKilometers - before.OffsetKilometers).Length, 0, .0001);
        Assert.True(Vector3d.Dot(before.Forward, after.Forward) > .999999);
        Assert.True(Vector3d.Dot(before.Up, after.Up) > .999999);
        Assert.Equal(original.Selection, restored.Selection);
        Assert.Equal(original.Epoch, restored.Epoch);
        Assert.Equal(33, restored.EffectiveFieldOfView, 6);
        Assert.Equal(-120, restored.PlaybackRate);
        Assert.False(restored.Playing);
        Assert.Equal(Delta, parsed.Scenario.DeltaT);
    }

    [Fact]
    public void UnsupportedLegacyExportsAreRejectedInsteadOfChangingTheFrame()
    {
        var original = new ObserverView(Epoch, CameraMode.Solar, null, false);
        Assert.Throws<InvalidDataException>(() => SceneLink.WriteLegacy(original, World, Delta));
        var camera = new NavigationCamera(default, 100, 0, 0, new("missing/body"), true);
        Assert.Throws<InvalidDataException>(() => SceneLink.WriteLegacy(original with { Navigation = camera }, World, Delta));
        var mismatched = camera with { Target = SolarSystemSample.MoonId,
            Placement = new(new(100, 0, 0), new(0, 1, 0), new(0, 0, 1))
                { Frame = NavigationFrame.Chase, Reference = SolarSystemSample.SunId } };
        Assert.Throws<InvalidDataException>(() => SceneLink.WriteLegacy(original with { Navigation = mismatched }, World, Delta));
    }
}
