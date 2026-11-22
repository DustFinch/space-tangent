// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LayoutOperationBoundaryTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidSplitSharesCannotMutateTheLiveLayout(double share)
    {
        var session=SharedClockTests.Create(); byte[] before=ScenarioFile.Encode(session.CaptureScenario(0));
        Assert.Throws<ArgumentOutOfRangeException>(()=>session.SplitView(0,ViewSplit.Stacked,0,share,false));
        session.ResizeViewDivider("",share);
        Assert.Equal(before,ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void FullLayoutRefusesAnotherSplitAndRemovingAnEarlierViewKeepsTheClockOwner()
    {
        var session=SharedClockTests.Create(); session.RemoveView(0); Assert.Equal(1,session.ViewCount);
        Assert.Throws<ArgumentOutOfRangeException>(()=>session.SplitView(0,(ViewSplit)999));
        while(session.ViewCount<ObserverLayout.MaximumViews) Assert.True(session.SplitView(0,ViewSplit.Stacked));
        Assert.False(session.SplitView(0,ViewSplit.Stacked));
        var scenario=session.CaptureScenario(session.ViewCount-1);
        session.RestoreScenario(scenario with {ClockOwner=session.ViewCount-1});
        session.RemoveView(0);
        Assert.Equal(session.ViewCount-1,session.CaptureScenario(0).ClockOwner);
        Assert.Equal(ObserverLayout.MaximumViews-1,session.ViewCount);
    }
}
