// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class CivilCalendarDisplayTests
{
    [Fact]
    public void CivilDayDoesNotUseTheNoonOrTdbBoundary()
    {
        var leap = TimeConversion.FromUtc(new(new(2016, 12, 31), 23, 59, 60), TimeScale.Tdb);
        Assert.Equal(new HistoricalDate(2016, 12, 31), CivilCalendarDisplay.CivilDay(leap, DeltaTModel.FixedOffset));
        Assert.Equal(new HistoricalDate(2017, 1, 1), CivilCalendarDisplay.CivilDay(leap.AddSeconds(1), DeltaTModel.FixedOffset));
        var historical = new HistoricalDate(1054, 7, 4);
        var epoch = SpaceTangent.Core.Observing.EarthCivilTime.FromModeledUt1(historical, 3600L * JulianInstant.NanosecondsPerSecond, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal(historical, CivilCalendarDisplay.CivilDay(epoch, DeltaTModel.MorrisonStephenson2004));
    }

    [Fact]
    public void SupplementaryLabelsIncludeTheEstablishedCalendarAnchors()
    {
        string modern = CivilCalendarDisplay.Format(new(2000, 1, 1));
        Assert.Contains("Julian: 1999-12-19", modern);
        Assert.Contains("Hebrew: 23 Tevet 5760", modern);
        Assert.Contains("Maya: 13.0.0.0.0", CivilCalendarDisplay.Format(new(2012, 12, 21)));
        Assert.Contains("ISO: 2020-W53", CivilCalendarDisplay.Format(new(2021, 1, 1)));
        Assert.Contains("Civil day: 1054-07-04", CivilCalendarDisplay.Format(new(1054, 7, 4)));
    }
}
