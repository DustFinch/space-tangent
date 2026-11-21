// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

/// <summary>Review repro for c478697, a240144, 2ab7f40 and 3858904 (app 3858904). Put in tests/SpaceTangent.Application.Tests/.
/// Needs SPACETANGENT_DE440 and SPACETANGENT_PLANET_TEXTURES pointing at a folder with pck00011.tpc.</summary>
public sealed class ObservingReviewRepro
{
    private static readonly JulianInstant October18 = Utc(2024, 10, 18);

    // 1. Visible tonight on other bodies: the noon-to-noon UTC window can open during local night (Mars) or contain only
    //    one Sun crossing (Moon). 3858904 returns NoDarkness although the Sun is far below the horizon.
    [DataTheory]
    [InlineData("sol/mars", 2024, 10, 19)] [InlineData("sol/mars", 2024, 10, 25)] [InlineData("sol/mars", 2024, 11, 4)]
    [InlineData("sol/moon", 2024, 10, 24)] [InlineData("sol/moon", 2024, 11, 8)]
    public void TonightOnAnotherBodyFindsItsDarkness(string body, int year, int month, int day)
    {
        var camera = new EarthObserverCamera(new(0, 0, 0), 0, 10) { Body = new ObjectId(body) };
        var observer = new ObserverView(Utc(year, month, day, 12), CameraMode.Surface, null, false) { EarthObserver = camera };
        var world = Full();
        var result = VisibleTonight.Calculate(world, observer, DeltaTModel.MorrisonStephenson2004);
        Assert.NotEqual(NightVisibility.NoDarkness, result.Visibility);
        Assert.NotEqual(NightVisibility.NoEphemeris, result.Visibility);
        Assert.NotNull(result.From);
        Assert.NotNull(result.To);
        Assert.True(result.To.Value.CompareTo(result.From.Value) > 0);
        for (int i = 0; i <= 12; i++)
        {
            double seconds = (double)result.To.Value.DifferenceNanoseconds(result.From.Value) / JulianInstant.NanosecondsPerSecond;
            var snapshot = world.Graph.Evaluate(result.From.Value.AddSeconds(seconds * i / 12), DeltaTModel.MorrisonStephenson2004);
            Assert.True(SurfaceObserver.TryFrame(world, camera, snapshot, DeltaTModel.MorrisonStephenson2004, out var frame));
            Assert.True(frame.Horizontal(snapshot.SolarSystem!.Value.Sun.PositionKilometers)!.Value.AltitudeDegrees <= result.SunLimitDegrees + .01);
        }
    }

    // 2. The best-night label must name the date whose plotted midnight was rated (3858904 rates the following midnight).
    [DataFact]
    public void BestNightMatchesItsPlottedMidnight()
    {
        var observer = new ObserverView(October18, CameraMode.Surface, new ObjectId("sol/jupiter"), false)
            { EarthObserver = EarthObserverCamera.Moscow };
        var result = ObserverCalculations.Calculate(new SimulationWorld(Ephemeris(), []), observer, DeltaTModel.MorrisonStephenson2004);
        var best = Assert.IsType<HistoricalDate>(result.BestNight);
        var row = result.Year.Single(item => item.Date == best);
        Assert.Equal(result.BestNightAltitude!.Value, row.Midnight.AltitudeDegrees!.Value, 6);
    }

    // 3. Local eclipse/pass/flare search must not treat a lunar site as an Earth site when the view is in space mode.
    [DataFact]
    public void LocalEventSearchRejectsANonEarthSiteInEveryMode()
    {
        var camera = new EarthObserverCamera(new(30, -100, 0), 0, 10) { Body = SolarSystemSample.MoonId };
        var space = new ObserverView(Utc(2024, 3, 25), CameraMode.Solar, null, false) { EarthObserver = camera };
        Assert.Throws<ArgumentException>(() => ObserverEventFinder.Next(Full(), space, DeltaTModel.MorrisonStephenson2004));
    }

    // 4. Replacing the world must revalidate the observing body; the session must be able to reload its own save.
    [DataFact]
    public void WorldReplacementRevalidatesTheObservingBody()
    {
        var camera = new EarthObserverCamera(new(10, 20, 0), 0, 10) { Body = PlanetCatalogue.MarsId };
        var view = new ObserverView(October18, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = camera };
        var session = new SimulationSession(Full(), October18, DeltaTModel.FixedOffset);
        session.RestoreScenario(new([view], false, 0, 0, DeltaTModel.FixedOffset));
        Assert.True(session.TryReplaceWorld(new SimulationWorld(Ephemeris(), [])));
        var saved = ScenarioFile.Encode(session.CaptureScenario(0));
        Assert.Null(Record.Exception(() => session.RestoreScenario(ScenarioFile.Decode(saved))));
    }

    // 5. Sites on installed SSC bodies: charts must use a graph that contains the body; a body without a rotation
    //    model must be reported as unavailable instead of getting a fixed ICRF frame.
    [DataFact]
    public void SscBodiesHaveChartsOrAnExplicitlyUnavailableFrame()
    {
        var root = Directory.CreateTempSubdirectory("st-review-ssc-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "a.ssc"), """
                "Spinworld" "Sol" { Radius 600 UniformRotation { Period 10 } EllipticalOrbit { Period 1000 SemiMajorAxis 2.7 } }
                "Stillworld" "Sol" { Radius 600 EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                """);
            var world = Full(SscBodyCatalogue.Load(root.FullName));
            var snapshot = world.Graph.Evaluate(October18, DeltaTModel.MorrisonStephenson2004);

            var spin = SurfaceObserver.Bodies(world).Single(item => item.Name == "Spinworld");
            var spinCamera = new EarthObserverCamera(new(30, 40, 0), 0, 10) { Body = spin.Id };
            var observer = new ObserverView(October18, CameraMode.Surface, SolarSystemSample.SunId, false) { EarthObserver = spinCamera };
            var charts = ObserverCalculations.Calculate(world, observer, DeltaTModel.MorrisonStephenson2004);
            Assert.Contains(charts.Year, item => item.Midnight.AltitudeDegrees is not null);

            var still = SurfaceObserver.Bodies(world).SingleOrDefault(item => item.Name == "Stillworld");
            if (still is not null)
                Assert.False(SurfaceObserver.TryFrame(world, new EarthObserverCamera(new(30, 40, 0), 0, 10) { Body = still.Id },
                    snapshot, DeltaTModel.MorrisonStephenson2004, out _));
        }
        finally { root.Delete(true); }
    }

    // 6. An unknown observing body must reject the whole scene, not only when the view is in surface mode.
    [DataFact]
    public void UnknownObservingBodyRejectsTheScene()
    {
        var camera = new EarthObserverCamera(new(10, 20, 0), 0, 10) { Body = new ObjectId("sol/nowhere") };
        var view = new ObserverView(October18, CameraMode.Solar, SolarSystemSample.SunId, false) { EarthObserver = camera };
        var bytes = ScenarioFile.Encode(new([view], false, 0, 0, DeltaTModel.FixedOffset));
        var session = new SimulationSession(Full(), October18, DeltaTModel.FixedOffset);
        Assert.ThrowsAny<Exception>(() => session.RestoreScenario(ScenarioFile.Decode(bytes)));
    }

    [DataFact]
    public void UnspecifiedRotationIsUnavailableWithoutHidingAnExplicitFixedFrame()
    {
        var root = Directory.CreateTempSubdirectory("st-review-orientation-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "b.ssc"), """
                "Stillworld" "Sol" { Radius 600 EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                "Fixedworld" "Sol" { Radius 600 FixedRotation {} EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                "Mixedworld" "Sol" { Radius 600 UniformRotation { Period 10 } Timeline [
                    { Ending 2460600 FixedRotation {} EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                    { Ending 2470000 EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                ] }
                """);
            var world = Full(SscBodyCatalogue.Load(root.FullName));
            var snapshot = world.Graph.Evaluate(October18);
            foreach (string name in new[] { "Stillworld", "Fixedworld", "Mixedworld" })
            {
                var body = SurfaceObserver.Bodies(world).Single(item => item.Name == name);
                var camera = new EarthObserverCamera(new(30, 40, 0), 0, 10) { Body = body.Id };
                Assert.Equal(name == "Fixedworld", SurfaceObserver.TryFrame(world, camera, snapshot, DeltaTModel.FixedOffset, out _));
            }
        }
        finally { root.Delete(true); }
    }

    [DataFact]
    public void BackgroundCalculationsNeverInvokeScriptedParentsOrTheirChildren()
    {
        var root = Directory.CreateTempSubdirectory("st-review-background-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "b.ssc"), """
                "Unsafe" "Sol" { Radius 600 UniformRotation { Period 10 } EllipticalOrbit { Period 1000 SemiMajorAxis 2.8 } }
                "Child" "Sol/Unsafe" { Radius 600 UniformRotation { Period 10 } EllipticalOrbit { Period 10 SemiMajorAxis 10000 } }
                "Safe" "Sol" { Radius 600 UniformRotation { Period 10 } EllipticalOrbit { Period 1000 SemiMajorAxis 2.7 } }
                """);
            int calls = 0;
            var definitions = SscBodyCatalogue.Load(root.FullName).Select(body => body.Name == "Unsafe" ? body with
            {
                UsesScriptedModels = true,
                Sample = _ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("Not a background callback"); }
            } : body).ToArray();
            var world = Full(definitions);
            foreach (string name in new[] { "Unsafe", "Child", "Safe" })
            {
                var body = SurfaceObserver.Bodies(world).Single(item => item.Name == name);
                var observer = new ObserverView(October18, CameraMode.Surface, SolarSystemSample.SunId, false)
                    { EarthObserver = new(new(0, 0, 0), 0, 10) { Body = body.Id } };
                if (name == "Safe")
                {
                    Assert.Contains(ObserverCalculations.Calculate(world, observer, DeltaTModel.FixedOffset).Year,
                        row => row.Midnight.AltitudeDegrees is not null);
                    var night = VisibleTonight.Calculate(world, observer, DeltaTModel.FixedOffset);
                    Assert.NotNull(night.From);
                    Assert.NotNull(night.To);
                }
                else
                {
                    Assert.Contains("background", Assert.Throws<ArgumentException>(() =>
                        ObserverCalculations.Calculate(world, observer, DeltaTModel.FixedOffset)).Message);
                    Assert.Contains("background", Assert.Throws<ArgumentException>(() =>
                        VisibleTonight.Calculate(world, observer, DeltaTModel.FixedOffset)).Message);
                }
                Assert.Equal(0, calls);
            }
        }
        finally { root.Delete(true); }
    }

    private static De440Ephemeris? _ephemeris;
    private static De440Ephemeris Ephemeris() => _ephemeris ??= De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
    private static SimulationWorld Full(IEnumerable<SscBodyDefinition>? ssc = null) => new(Ephemeris(), [],
        planets: new PlanetEphemerides(Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES")!), bodyCatalog: ssc);
    private static JulianInstant Utc(int year, int month, int day, int hour = 0) =>
        TimeConversion.FromUtc(new(new(year, month, day), hour, 0), TimeScale.Tdb);

    private static string? MissingData()
    {
        string? root = Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES");
        return File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) && File.Exists(Path.Combine(root ?? "", "pck00011.tpc"))
            ? null : "Set SPACETANGENT_DE440 and SPACETANGENT_PLANET_TEXTURES with pck00011.tpc.";
    }

    private sealed class DataFactAttribute : FactAttribute { public DataFactAttribute() => Skip = MissingData(); }
    private sealed class DataTheoryAttribute : TheoryAttribute { public DataTheoryAttribute() => Skip = MissingData(); }
}
