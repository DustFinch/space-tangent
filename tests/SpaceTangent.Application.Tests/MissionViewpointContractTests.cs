// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class MissionViewpointContractTests
{
    [Theory]
    [InlineData(false, 0, 0)] [InlineData(true, 0, 0)]
    [InlineData(false, 90, 0)] [InlineData(true, 90, 0)]
    [InlineData(false, 0, 90)] [InlineData(true, 0, 90)]
    [InlineData(false, 0, -90)] [InlineData(true, 0, -90)]
    public void OpeningAViewpointUsesItsDeclaredPoleAltitudeAndLocalAngles(bool equatorial, double azimuth, double elevation)
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0); session.Step(1, 3600);
        var other = session[1]; var model = session.DeltaT;
        var earth = SolarSystemSample.EarthId;
        var viewpoint = new MissionViewpoint("Authored witness", earth, null, 1000, azimuth, elevation, equatorial);
        Assert.True(session.OpenMissionViewpoint(0, viewpoint));
        var camera = session[0].Navigation!.Value;
        var pose = camera.Placement!.Value;
        double tilt = equatorial ? 0 : EclipticJ2000.ObliquityRadians;
        Vector3d pole = new(0, -Math.Sin(tilt), Math.Cos(tilt));
        Vector3d x = new(1, 0, 0), y = new(0, Math.Cos(tilt), Math.Sin(tilt));
        double a = azimuth * Math.PI / 180, e = elevation * Math.PI / 180;
        Vector3d expected = (x * Math.Cos(a) + y * Math.Sin(a)) * Math.Cos(e) + pole * Math.Sin(e);
        Assert.Equal(earth, session[0].Selection);
        Assert.False(camera.Tracking);
        Assert.False(session.AnyFlight);
        Assert.Equal(1000 + session.World.TargetRadiusKilometers(earth), camera.RangeKilometers);
        Assert.InRange((pose.OffsetKilometers / camera.RangeKilometers - expected).Length, 0, 1e-12);
        Assert.InRange((pose.Forward + expected).Length, 0, 1e-12);
        Assert.InRange(Math.Abs(Vector3d.Dot(pose.Forward, pose.Up)), 0, 1e-12);
        Assert.InRange(Math.Abs(pose.Up.Length - 1), 0, 1e-12);
        Assert.Equal(other, session[1]); Assert.Equal(model, session.DeltaT);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("negative-range")]
    [InlineData("nonfinite-altitude")]
    public void UnavailableOrInvalidViewpointsPreserveAllObserversAndClocks(string fault)
    {
        var session = SharedClockTests.Create(); session.Split(); var before = session.CaptureScenario(0);
        var viewpoint = new MissionViewpoint("Unavailable", fault == "absent" ? new("absent") : SolarSystemSample.EarthId,
            null, fault == "negative-range" ? -1e9 : fault == "nonfinite-altitude" ? double.NaN : 1000, 0, 0, true);
        Assert.False(session.OpenMissionViewpoint(0, viewpoint));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(session.CaptureScenario(0)));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void AZeroLengthReferenceUsesAFiniteOrthogonalFallback(bool missing)
    {
        var session = SharedClockTests.Create(); var earth = SolarSystemSample.EarthId;
        Assert.True(session.OpenMissionViewpoint(0, new("Reference witness", earth, missing ? new("absent") : earth, 1000, 0, 0, true)));
        var camera = session[0].Navigation!.Value; var pose = camera.Placement!.Value;
        Assert.True(camera.IsValid());
        Vector3d expected = missing ? new(1, 0, 0) : new(0, 1, 0);
        Assert.InRange((pose.OffsetKilometers / camera.RangeKilometers - expected).Length, 0, 1e-12);
    }
}
