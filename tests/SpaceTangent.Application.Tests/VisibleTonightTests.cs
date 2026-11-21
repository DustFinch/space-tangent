// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class VisibleTonightTests
{
    [TonightFact]
    [Trait("Category", "ExternalData")]
    public void WinterNightContainsSiriusAndMagnitudeQualifiedMessierObjects()
    {
        var directory = Directory.CreateTempSubdirectory("st-tonight-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "test.dsc"), """
                Galaxy "M 31:Andromeda" { Type "Sb" RA 0.7122 Dec 41.2689 Distance 2573000 Radius 70760 AbsMag -20.12 }
                Galaxy "M 99:Too faint" { RA 12.3 Dec 14.4 Distance 60000000 Radius 10000 AbsMag -10 }
                """);
            var stars = BrightStarCatalogue.Read(new StringReader(
                "hip\tname\tra_rad\tdec_rad\tmu_alpha_cos_delta_rad_y\tmu_delta_rad_y\tv_mag\n" +
                FormattableString.Invariant($"32349\tSirius\t{101.28715533 * Math.PI / 180:R}\t{-16.716116 * Math.PI / 180:R}\t0\t0\t-1.46\n")));
            var world = new SimulationWorld(Ephemeris(), [], brightStars: stars, deepSky: DeepSkyIndex.Load(directory.FullName));
            var camera = EarthObserverCamera.Moscow with { CivilClock = new("Moscow", "Europe/Moscow", 3) };
            var observer = new ObserverView(TimeConversion.FromUtc(new(new(2024, 1, 15), 16), TimeScale.Tdb),
                CameraMode.Surface, null, false) { EarthObserver = camera };
            var result = VisibleTonight.Calculate(world, observer, DeltaTModel.MorrisonStephenson2004);
            Assert.Equal(NightVisibility.Visible, result.Visibility);
            Assert.Equal(new HistoricalDate(2024, 1, 15), result.Date);
            Assert.Equal(-12, result.SunLimitDegrees);
            var sirius = Assert.Single(result.Targets, item => item.Name == "Sirius");
            Assert.InRange(sirius.BestDirection.AltitudeDegrees, 17, 18);
            Assert.InRange(TimeConversion.ToUtc(sirius.Best).Hour, 20, 22);
            Assert.Contains(result.Targets, item => item.Name == "M 31");
            Assert.DoesNotContain(result.Targets, item => item.Name == "M 99");
            Assert.Equal(result.Targets.OrderBy(item => item.Best).ThenBy(item => item.Magnitude), result.Targets);
            Assert.All(result.Targets, item =>
            {
                Assert.True(item.Best.CompareTo(result.From!.Value) >= 0 && item.Best.CompareTo(result.To!.Value) <= 0);
                Assert.InRange(item.BestDirection.AltitudeDegrees, 10, 90);
            });
            var afterMidnight = VisibleTonight.Calculate(world, observer with
                { Epoch = TimeConversion.FromUtc(new(new(2024, 1, 16), 0), TimeScale.Tdb) }, DeltaTModel.MorrisonStephenson2004);
            Assert.Equal(result.Date, afterMidnight.Date);
            Assert.Equal(result.From, afterMidnight.From);
            Assert.Throws<OperationCanceledException>(() => VisibleTonight.Calculate(world, observer, DeltaTModel.FixedOffset, new(true)));
        }
        finally { directory.Delete(true); }
    }

    [TonightFact]
    [Trait("Category", "ExternalData")]
    public void PolarDayAndMissingEphemerisHaveDifferentResults()
    {
        var ephemeris = Ephemeris();
        var world = new SimulationWorld(ephemeris, []);
        var observer = new ObserverView(TimeConversion.FromUtc(new(new(2024, 6, 21), 12), TimeScale.Tdb),
            CameraMode.Surface, null, false) { EarthObserver = EarthObserverCamera.Tromso };
        Assert.Equal(NightVisibility.NoDarkness, VisibleTonight.Calculate(world, observer, DeltaTModel.FixedOffset).Visibility);
        Assert.Equal(NightVisibility.NoEphemeris, VisibleTonight.Calculate(world, observer with
            { Epoch = ephemeris.End.AddSeconds(-1800) }, DeltaTModel.FixedOffset).Visibility);
    }

    private static De440Ephemeris Ephemeris() => De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
    private sealed class TonightFactAttribute : FactAttribute
    {
        public TonightFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the real DE440 ephemeris.";
        }
    }
}
