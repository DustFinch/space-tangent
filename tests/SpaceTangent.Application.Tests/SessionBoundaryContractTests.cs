// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SessionBoundaryContractTests
{
    private static readonly ObjectId Missing = new("missing/object");

    [Theory]
    [InlineData("active")]
    [InlineData("clock")]
    [InlineData("empty")]
    [InlineData("delta-t")]
    [InlineData("epoch")]
    [InlineData("mode")]
    [InlineData("mission")]
    [InlineData("timeline")]
    [InlineData("probe")]
    [InlineData("lunar-terrain")]
    [InlineData("mars-terrain")]
    [InlineData("satellites")]
    [InlineData("earth-camera")]
    [InlineData("field")]
    [InlineData("optics")]
    [InlineData("rate")]
    [InlineData("marks")]
    [InlineData("terrain-camera")]
    [InlineData("script-target")]
    [InlineData("navigation")]
    public void RejectedScenarioRestoresLeaveEveryLiveObserverAndClockUntouched(string fault)
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0);
        session.Step(1, 3600); session.SetFieldOfView(1, 33);
        var before = session.CaptureScenario(1); var view = before.Views[0];
        var bad = fault switch
        {
            "active" => before with { ActiveView = -1 },
            "clock" => before with { ClockOwner = 9 },
            "empty" => before with { Views = [] },
            "delta-t" => before with { DeltaT = (DeltaTModel)999 },
            _ => before with { Views = [fault switch
            {
                "epoch" => view with { Epoch = session.World.Ephemeris.Start.AddSeconds(-1) },
                "mode" => view with { Mode = (CameraMode)999 },
                "mission" => view with { Mode = CameraMode.Mission },
                "timeline" => view with { Mode = CameraMode.Timeline },
                "probe" => view with { Mode = CameraMode.Probe },
                "lunar-terrain" => view with { Mode = CameraMode.LunarTerrain },
                "mars-terrain" => view with { Mode = CameraMode.MarsTerrain },
                "satellites" => view with { Mode = CameraMode.Satellites },
                "earth-camera" => view with { EarthObserver = EarthObserverCamera.Moscow with { Site = new(91, 0, 0) } },
                "field" => view with { FieldOfViewDegrees = 0 },
                "optics" => view with { Optics = new(ObserverOpticsLibrary.Default with { Barlow = 0 }, true) },
                "rate" => view with { PlaybackRate = double.PositiveInfinity },
                "marks" => view with { ReferenceMarks = (BodyReferenceMarks)(1 << 30) },
                "terrain-camera" => view with { Terrain = new(181, 0, 100) },
                "script-target" => view with { ScriptAimTarget = Missing },
                "navigation" => view with { Navigation = new(default, 100, 0, 0, Missing, true) },
                _ => throw new ArgumentException(fault)
            }, before.Views[1]] }
        };
        Assert.Throws<InvalidDataException>(() => session.RestoreScenario(bad));
        Assert.Equal(ScenarioFile.Encode(before), ScenarioFile.Encode(session.CaptureScenario(1)));
    }

    [Theory]
    [InlineData(CameraMode.Mission)]
    [InlineData(CameraMode.Timeline)]
    [InlineData(CameraMode.Probe)]
    [InlineData(CameraMode.Satellites)]
    [InlineData(CameraMode.LunarTerrain)]
    [InlineData(CameraMode.MarsTerrain)]
    public void UnavailableCameraModesDoNotAlterTheActiveObserver(CameraMode mode)
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        Assert.Throws<InvalidOperationException>(() => session.SetMode(0, mode));
        Assert.Equal(ScenarioFile.Encode(before), ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void InvalidPublicControlsLeaveTheSessionUnmodified()
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        Action[] actions = [
            () => session.SetEarthObserver(0, EarthObserverCamera.Moscow with { Site = new(0, 181, 0) }),
            () => session.SetOptics(0, new(ObserverOpticsLibrary.Default with { Barlow = 0 }, true)),
            () => session.SetSplitLayout((ViewSplit)999), () => session.SetFieldOfView(0, double.NaN),
            () => session.SetDeltaT((DeltaTModel)999), () => session.SetMode(0, (CameraMode)999),
            () => session.SetLunarTerrainCamera(0, new(0, -91, 100)),
            () => session.SelectObject(0, Missing), () => session.SetPlaybackRate(0, double.PositiveInfinity),
            () => session.Seek(0, session.World.Ephemeris.End.AddSeconds(1)), () => session.Step(0, double.NaN)
        ];
        foreach (var action in actions)
        {
            Assert.Throws<ArgumentOutOfRangeException>(action);
            Assert.Equal(ScenarioFile.Encode(before), ScenarioFile.Encode(session.CaptureScenario(0)));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationSession(session.World, session.World.Ephemeris.Start.AddSeconds(-1), session.DeltaT));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SteppingPastEphemerisClampsAndStopsOnlyTheIndependentClock(bool end)
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0);
        session.SetPlaying(0, true); var other = session[1];
        session.Step(0, end ? double.MaxValue : -double.MaxValue);
        Assert.Equal(end ? session.World.Ephemeris.End : session.World.Ephemeris.Start, session[0].Epoch);
        Assert.False(session[0].Playing);
        Assert.Equal(other, session[1]);
    }

    [Fact]
    public void SplitOrientationAndClosingTheSecondViewPreserveTheRemainingObserver()
    {
        var session = SharedClockTests.Create(); session.SetSplitLayout(ViewSplit.Stacked);
        session.Split(); session.SetSplitLayout(ViewSplit.SideBySide);
        Assert.Equal(ViewSplit.SideBySide, session.Layout.Direction);
        session.SetLunarTerrainCamera(0, LunarTerrainCamera.Copernicus);
        Assert.True(session[0].Terrain!.Value.IsValid()); var first = session[0];
        session.CloseSecondView();
        Assert.Equal(1, session.ViewCount); Assert.Equal(first, session[0]);
        session.CloseSecondView();
        Assert.Equal(1, session.ViewCount); Assert.Equal(first, session[0]);
    }
}
