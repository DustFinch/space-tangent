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

    [NativeQueryFact(true)]
    public void OriginalGalaxyScriptMarksTheFullCatalogueAndRespondsToSelection()
    {
        string sources = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        string dsc = Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT") ??
            throw new InvalidOperationException("Set SPACETANGENT_DSC_ROOT to the original deep-sky catalogues.");
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var world = new SimulationWorld(ephemeris, [], deepSky: DeepSkyIndex.Load(dsc));
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        var untouched = new ObserverView(epoch.AddSeconds(3600), CameraMode.Solar, SolarSystemSample.EarthId, false);
        session.RestoreScenario(new([session[0] with { Playing = false }, untouched], false, 0, 0, session.DeltaT));
        var milkyWay = new ObjectId("dso/milky-way");
        var andromeda = new ObjectId("dso/m31");
        var messages = new List<string>();
        string source = Path.Combine(sources, "z-dist.celx");
        Assert.Equal("A88301D06F858374DB73047CBDEAD212A9D36BD27C6E297D2F686E32C91D29BB",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))));
        var script = LuaSessionScript.Load(source, session, 0, false, messages.Add);
        for (int i = 0; i < 6000 && messages.Count == 0 && !script.Finished; i++)
            script.Step(.05);
        Assert.Null(script.Error);
        Assert.False(script.Finished);
        Assert.Equal(milkyWay, session[0].Selection);
        int galaxies = world.DeepSky.Objects.Count(body => body.Kind == DeepSkyKind.Galaxy);
        Assert.True(galaxies > 10_000);
        Assert.Equal(galaxies, session.Markers(0).Count);
        Assert.Empty(session.Markers(1));
        Assert.Equal(1, session.Markers(0)[andromeda].Size);
        Assert.Contains("redshift z =", Assert.Single(messages));

        var snapshot = world.Graph.Evaluate(epoch);
        session.UpdateCamera(0, snapshot, 5);
        Assert.False(session.IsFlying(0));
        Assert.InRange(Math.Abs(session[0].Navigation!.Value.RangeKilometers / 3.08568025e22 - 1), 0, 1e-12);
        session.SelectObject(0, andromeda);
        for (int i = 0; i < 200; i++)
            script.Step(.05);
        Assert.Null(script.Error);
        Assert.False(script.Finished);
        Assert.Equal(2, messages.Count);
        Assert.Contains("redshift z =", messages[^1]);
        Assert.Contains("max. redshift:", messages[^1]);
        Assert.Equal(new ScriptMarker(new(0, 1, 0), 10, 1), session.Markers(0)[andromeda]);
        Assert.False(session.Markers(0).ContainsKey(milkyWay));
        Assert.Equal(untouched, session[1]);
        script.Stop();
        Assert.True(script.Finished);
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
        public NativeQueryFactAttribute(bool deepSky = false)
        {
            if (new[] { "SPACETANGENT_TOUR_SOURCES", "SPACETANGENT_STELLAR_ARCHIVE", "SPACETANGENT_DE440" }
                .Any(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the original tour, stellar archive and ephemeris paths.";
            else if (deepSky && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT")))
                Skip = "Set SPACETANGENT_DSC_ROOT to the original deep-sky catalogues.";
        }
    }
}
