// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NativeScriptQueryTests
{
    [NativeQueryFact]
    public void OriginalDistanceMagnitudeAndYearScriptsUseTheActiveObserver()
    {
        string sources = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")!;
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var world = new SimulationWorld(ephemeris, [], stellar: StellarCatalogue.LoadArchive(archive));
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var vega = new ObjectId("hip/91262");
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        var observer = new ObserverView(epoch, CameraMode.Solar, vega, false)
        {
            Navigation = new(new Vector3d(2e7, 3e7, 5e7), 100, 0, 0, null, false)
                { Placement = new NavigationPlacement(new(100, 0, 0), new(-1, 0, 0), new(0, 0, 1)) }
        };
        var untouched = new ObserverView(epoch.AddSeconds(3600), CameraMode.Solar, SolarSystemSample.EarthId, false);
        session.RestoreScenario(new([observer, untouched], false, 0, 0, session.DeltaT));
        var vegaEntry = world.Stellar.Find(vega)!;
        double magnitude = vegaEntry.AbsoluteMagnitude ?? vegaEntry.VisualMagnitude + 5 - 5 * Math.Log10(vegaEntry.DistanceParsecs);
        Assert.True(world.TryNavigationState(vega, world.Graph.Evaluate(epoch), out var star));
        Vector3d eye = observer.Navigation!.Value.AnchorKilometers + observer.Navigation.Value.Placement!.Value.OffsetKilometers;
        double expectedDistance = (star.PositionKilometers - eye).Length;
        Assert.InRange(Math.Abs(NumberAfterEquals(Run("distanceto.celx"), " km") / expectedDistance - 1), 0, 1e-12);
        Assert.InRange(Math.Abs(NumberAfterEquals(Run("absmag.celx"), "") - magnitude), 0, 1e-12);
        Assert.Empty(Run("annum.celx"));
        Assert.Equal(epoch.AddSeconds(365 * JulianInstant.SecondsPerDay), session[0].Epoch);
        Assert.Equal(untouched, session[1]);

        List<string> Run(string name)
        {
            var messages = new List<string>();
            var script = LuaSessionScript.Load(Path.Combine(sources, name), session, 0, false, messages.Add);
            for (int frame = 0; !script.Finished && frame < 800; frame++)
                script.Step(.05);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            return messages;
        }
    }

    private static double NumberAfterEquals(List<string> messages, string suffix)
    {
        string line = Assert.Single(messages);
        Assert.EndsWith(suffix, line, StringComparison.Ordinal);
        string number = line[(line.LastIndexOf('=') + 1)..(line.Length - suffix.Length)].Trim();
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return result;
        return double.Parse(number, NumberStyles.Float, CultureInfo.CurrentCulture);
    }

    private sealed class NativeQueryFactAttribute : FactAttribute
    {
        public NativeQueryFactAttribute()
        {
            if (new[] { "SPACETANGENT_TOUR_SOURCES", "SPACETANGENT_STELLAR_ARCHIVE", "SPACETANGENT_DE440" }
                .Any(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the original tour, stellar archive and ephemeris paths.";
        }
    }
}
