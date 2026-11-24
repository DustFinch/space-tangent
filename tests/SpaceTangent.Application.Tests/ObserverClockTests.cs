// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverClockTests
{
    private static readonly JulianInstant Epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
    private static List<ObserverView> Views() =>
        [new(Epoch, CameraMode.Solar, null, false), new(Epoch.AddSeconds(7200), CameraMode.Satellites, null, true)];

    [Fact]
    public void IndependentClocksLeaveTheOtherObserversFieldsUntouched()
    {
        var views = Views(); var clock = new ObserverClock(views); clock.Restore(false, 0);
        ObserverView other = views[1];
        clock.SetPlaybackRate(0, -180, synchronizeRate: true);
        clock.Step(0, -60.5, Epoch.AddSeconds(-1000), Epoch.AddSeconds(1000));
        Assert.Equal(-60_500_000_000L, views[0].Epoch.DifferenceNanoseconds(Epoch));
        Assert.True(views[0].Playing);
        Assert.Equal(-180, clock.CurrentPlaybackRate(0));
        Assert.Equal(other, views[1]);
    }

    [Fact]
    public void JoiningClocksUsesTheChosenObserversRateAndLightTimeState()
    {
        var views = Views();
        views[1] = views[1] with { PlaybackRate = -600, LightTimeDelayed = true, LightTimeOffsetNanoseconds = 12345 };
        var clock = new ObserverClock(views); clock.Restore(false, 0);
        clock.Synchronize(true, 1);
        Assert.Equal(1, clock.Owner);
        Assert.True(clock.Synchronized);
        foreach (var view in views)
        {
            Assert.Equal(Epoch.AddSeconds(7200), view.Epoch);
            Assert.Equal(-600, view.PlaybackRate);
            Assert.True(view.Playing);
            Assert.True(view.LightTimeDelayed);
            Assert.Equal(12345, view.LightTimeOffsetNanoseconds);
        }
    }

    [Fact]
    public void ASharedStopRetainsThePreviousOwnersDefaultRateBeforeChangingOwner()
    {
        var views = Views(); var clock = new ObserverClock(views);
        clock.SetPlaybackRate(1, 0, synchronizeRate: true);
        Assert.Equal(1, clock.Owner);
        Assert.All(views, view => Assert.False(view.Playing));
        Assert.All(views, view => Assert.Equal(JulianInstant.SecondsPerDay, view.PlaybackRate));
    }

    [Fact]
    public void AnUncoordinatedScriptRateDoesNotReplaceAnotherSharedClockOwnersRate()
    {
        var views = Views(); var clock = new ObserverClock(views);
        clock.SetPlaybackRate(1, 10, synchronizeRate: false);
        Assert.All(views, view => Assert.Equal(JulianInstant.SecondsPerDay, view.PlaybackRate));
        Assert.Equal(1, clock.Owner);
    }

    [Fact]
    public void StoppingAnIndependentClockLeavesItsRateUnsetAndOtherViewPlaying()
    {
        var views = Views(); var clock = new ObserverClock(views); clock.Restore(false, 0);
        clock.SetPlaybackRate(0, 0, synchronizeRate: true);
        Assert.Null(views[0].PlaybackRate);
        Assert.False(views[0].Playing);
        Assert.True(views[1].Playing);
        Assert.Equal(180, clock.CurrentPlaybackRate(1));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CoverageClampingStopsOnlyTheChosenIndependentClock(bool forward)
    {
        var views = Views(); var clock = new ObserverClock(views); clock.Restore(false, 0);
        ObserverView other = views[1];
        clock.Step(0, forward ? double.MaxValue : -double.MaxValue, Epoch.AddSeconds(-100), Epoch.AddSeconds(100));
        Assert.Equal(Epoch.AddSeconds(forward ? 100 : -100), views[0].Epoch);
        Assert.False(views[0].Playing);
        Assert.Equal(other, views[1]);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)] [InlineData(100_000_001)]
    public void InvalidRatesAreRejectedBeforeAnyObserverChanges(double rate)
    {
        var views = Views(); var before = views.ToArray(); var clock = new ObserverClock(views);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetPlaybackRate(1, rate, true));
        Assert.Equal(before, views.ToArray());
    }

    [Fact]
    public void RemovingAnObserverRemapsTheOwnerAndRemovingTheOwnerChoosesTheFirst()
    {
        var views = Views(); views.Add(views[0]); var clock = new ObserverClock(views);
        clock.SetClock(2, Epoch, false);
        views.RemoveAt(0); clock.OnViewRemoved(0);
        Assert.Equal(1, clock.Owner);
        views.RemoveAt(1); clock.OnViewRemoved(1);
        Assert.Equal(0, clock.Owner);
    }

    [Fact]
    public void ScenarioMetadataRestoreDoesNotRewriteObserverDataBeforeTheSessionCommit()
    {
        var views = Views(); var before = views.ToArray(); var clock = new ObserverClock(views);
        clock.Restore(true, 1);
        Assert.True(clock.Synchronized);
        Assert.Equal(1, clock.Owner);
        Assert.Equal(before, views.ToArray());
    }
}
