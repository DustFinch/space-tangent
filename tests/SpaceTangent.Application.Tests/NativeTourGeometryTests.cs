// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class NativeTourGeometryTests
{
    [TourArchiveFact]
    public void OriginalLocationOrbitAndUpVectorToursRetainTheirGeometry()
    {
        string sources = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var model = DeltaTModel.MorrisonStephenson2004;
        var baseline = new ObserverView(epoch.AddSeconds(120), CameraMode.Solar, SolarSystemSample.MoonId, false);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var locations = Run("gotoloc.cel");
            var relative = locations.Poses.First(pose => pose.Target == PlanetCatalogue.MarsId &&
                Math.Abs(pose.RangeKilometers - 106378.1) < .001);
            var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
            var expected = new Vector3d(0, -sin * 106378.1, cos * 106378.1);
            Assert.InRange((relative.Placement!.Value.OffsetKilometers - expected).Length, 0, 1e-6);
            var absolute = locations.Final.Navigation!.Value;
            Vector3d sun = world.Ephemeris.Sample(epoch).Sun.PositionKilometers;
            Vector3d archived = new(30988223546.628002, -70180951.86538383, -30417101.389447883);
            Assert.InRange((absolute.AnchorKilometers - sun - archived).Length, 0, .0001);
            Assert.Null(absolute.Target);
            Assert.Equal(default, absolute.Placement!.Value.OffsetKilometers);

            var circle = Run("orbit.cel");
            var beginning = circle.Poses.First(pose => pose.Target == PlanetCatalogue.MarsId);
            var ended = circle.Final.Navigation!.Value;
            Assert.Equal(beginning.RangeKilometers, ended.RangeKilometers, 6);
            var before = beginning.Placement!.Value;
            var after = ended.Placement!.Value;
            double angle = -50 * Math.PI / 180;
            Assert.InRange(Math.Abs(Vector3d.Dot(before.Forward, after.Forward) -
                (Math.Cos(angle) + (1 - Math.Cos(angle)) / 3)), 0, 2e-12);

            var geographic = Run("goto.cel");
            var surface = geographic.Final.Navigation!.Value;
            Assert.Equal(SolarSystemSample.EarthId, surface.Target);
            var site = new EarthSite(30.983344, 52.357715, 0);
            var lighting = EarthSurfaceLighting.Sample(world.Ephemeris.Sample(epoch), site, model);
            var placement = surface.Placement!.Value;
            Assert.InRange((placement.OffsetKilometers / surface.RangeKilometers - lighting.EastNorthUp.Z).Length, 0, 1e-12);
            Assert.InRange(Math.Abs(Vector3d.Dot(placement.Up, placement.Forward)), 0, 1e-12);

            string demo = File.ReadAllText(Path.Combine(sources, "demo.cel"));
            var commands = Regex.Matches(demo, @"changedistance\s*\{\s*duration\s+([0-9.]+)\s+rate\s+([0-9.]+)\s*\}");
            Assert.Equal(3, commands.Count);
            var initial = new SimulationSession(world, epoch, model);
            initial.OpenSpaceView(0);
            var starting = initial[0].Navigation!.Value;
            for (int index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                string dolly = Path.Combine(folder.FullName, $"distance-{index}.cel");
                File.WriteAllText(dolly, "{ " + command.Value + " }");
                double seconds = double.Parse(command.Groups[1].Value, CultureInfo.InvariantCulture);
                double rate = double.Parse(command.Groups[2].Value, CultureInfo.InvariantCulture);
                var result = Run(dolly, uneven: true);
                var completed = result.Final.Navigation!.Value;
                Assert.Equal(starting.Target, completed.Target);
                Assert.Equal(starting.ResolvePlacement(world.Graph.Evaluate(epoch)).Forward, completed.Placement!.Value.Forward);
                Assert.Contains(result.Poses, pose => pose.RangeKilometers > starting.RangeKilometers &&
                    pose.RangeKilometers < starting.RangeKilometers * Math.Exp(rate * seconds) * .999);
                Assert.InRange(completed.RangeKilometers / (starting.RangeKilometers * Math.Exp(rate * seconds)), 1 - 1e-11, 1 + 1e-11);
            }
        }
        finally { folder.Delete(recursive: true); }

        (ObserverView Final, List<NavigationCamera> Poses) Run(string name, bool uneven = false)
        {
            var session = new SimulationSession(world, epoch, model);
            session.OpenSpaceView(0);
            session.RestoreScenario(new([session[0], baseline], false, 0, 0, model));
            var converted = TourConverter.ConvertFile(Path.Combine(sources, name));
            Assert.Empty(converted.Unapplied);
            string lua = Path.Combine(folder.FullName, name + ".lua");
            File.WriteAllText(lua, converted.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            var poses = new List<NavigationCamera>();
            for (int frame = 0; (!script.Finished || session.IsFlying(0)) && frame < 800; frame++)
            {
                double elapsed = uneven ? (frame % 3) switch { 0 => .07, 1 => .19, _ => .31 } : .125;
                script.Step(elapsed);
                session.UpdateCamera(0, world.Graph.Evaluate(session[0].Epoch), elapsed);
                if (!session.IsFlying(0) && session[0].Navigation is { } pose)
                    poses.Add(pose);
            }
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.False(session.IsFlying(0));
            Assert.Equal(baseline, session[1]);
            return (session[0], poses);
        }
    }

    [TourArchiveFact]
    public void OriginalDemoAppliesCloudAndOrbitFlagsAndReportsRemainingLines()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var folder = Directory.CreateTempSubdirectory("st-tour-flags-");
        try
        {
            foreach (string name in new[] { "demo.cel", "demo_ru.cel" })
            {
                string source = Path.Combine(root, name);
                var conversion = TourConverter.ConvertFile(source);
                var lines = File.ReadAllLines(source);
                int render = Array.FindIndex(lines, line => line.Contains("renderflags { set", StringComparison.Ordinal)) + 1;
                Assert.Contains($"line {render}: renderflags planets", conversion.Unapplied);
                Assert.DoesNotContain(conversion.Unapplied, note => note.EndsWith("renderflags cloudmaps", StringComparison.Ordinal) ||
                    note.EndsWith("renderflags orbits", StringComparison.Ordinal));
                string lua = Path.Combine(folder.FullName, name + ".lua");
                File.WriteAllText(lua, conversion.Lua);
                var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
                session.OpenSpaceView(0);
                var other = session[0] with { Epoch = epoch.AddSeconds(1), Playing = false };
                session.RestoreScenario(new([session[0], other], false, 0, 0, session.DeltaT));
                var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
                script.Step(0);
                Assert.True(session[0].Sky.HideClouds);
                Assert.True(session[0].Sky.HideOrbits);
                Assert.Equal(other, session[1]);

                // Execute the original toggles separately so the assertions do not depend on flight timings.
                var flags = Regex.Matches(string.Join('\n', lines), @"renderflags\s*\{[^}]*\}")
                    .Select(match => match.Value).Where(command => command.Contains("orbits", StringComparison.Ordinal) ||
                        command.Contains("cloudmaps", StringComparison.Ordinal)).ToArray();
                Assert.Equal(4, flags.Length);
                foreach (string command in flags.Skip(1))
                {
                    string fixture = Path.Combine(folder.FullName, "flag.cel");
                    File.WriteAllText(fixture, "{ " + command + " }");
                    File.WriteAllText(lua, TourConverter.ConvertFile(fixture).Lua);
                    var step = LuaSessionScript.Load(lua, session, 0, false, _ => { });
                    step.Step(0);
                    Assert.True(step.Finished);
                    Assert.Null(step.Error);
                    if (command.Contains("cloudmaps", StringComparison.Ordinal))
                        Assert.False(session[0].Sky.HideClouds);
                    if (command.Contains("orbits", StringComparison.Ordinal))
                        Assert.Equal(command.Contains("clear", StringComparison.Ordinal), session[0].Sky.HideOrbits);
                    Assert.Equal(other, session[1]);
                }
            }
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicTourRestoresUtcAndFieldOfViewInItsOwnObserver()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.Split();
        session.RestoreScenario(new([session[0], session[1] with { FieldOfViewDegrees = 62 }], false, 0, 0, session.DeltaT));
        var other = session[1];
        var folder = Directory.CreateTempSubdirectory("st-cel-view-");
        try
        {
            string source = Path.Combine(folder.FullName, "view.cel");
            File.WriteAllText(source, """
                {
                  time { utc "2004-07-01T02:34:56.125+02:00" }
                  set { name "FOV" value 36 }
                  wait { duration 1 }
                  set { name "FOV" value 999 }
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Equal(["line 5: set FOV outside the supported range"], conversion.Unapplied);
            string lua = Path.Combine(folder.FullName, "view.lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            script.Step(0);
            var expected = TimeConversion.FromUtc(new(new(2004, 7, 1), 0, 34, 56, 125_000_000), TimeScale.Tdb);
            Assert.InRange((long)session[0].Epoch.DifferenceNanoseconds(expected), -25_000, 25_000);
            Assert.Equal(36, session[0].EffectiveFieldOfView, 12);
            Assert.Equal(other, session[1]);
            script.Step(1);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.Equal(36, session[0].EffectiveFieldOfView, 12);
            Assert.Equal(other, session[1]);

            File.WriteAllText(source, """{ overlay { filename "banner.png" fitscreen true colortop [1 0 0] } }""");
            conversion = TourConverter.ConvertFile(source);
            Assert.Equal(["banner.png"], conversion.MediaFiles);
            Assert.Equal(["line 1: overlay colortop", "line 1: overlay fitscreen"], conversion.Unapplied);
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicMarkersPreserveStyleAndStayInTheirOwnObserver()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.Split();
        var folder = Directory.CreateTempSubdirectory("st-cel-markers-");
        try
        {
            string source = Path.Combine(folder.FullName, "marks.cel");
            File.WriteAllText(source, """
                {
                  renderflags { set "markers" }
                  select { object "Sol/Earth" }
                  mark { color [1 0.5 0] size 16 alpha 0.6 }
                  mark { object "Sol/Earth/Moon" symbol "filledsquare" color "#0000ff" }
                  wait { duration 1 }
                  unmark { object "Sol/Earth/Moon" }
                  wait { duration 1 }
                  unmarkall {}
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Empty(conversion.Unapplied);
            string lua = Path.Combine(folder.FullName, "marks.lua");
            File.WriteAllText(lua, conversion.Lua);
            var other = LuaSessionScript.Load(lua, session, 1, false, _ => { });
            other.Step(0);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            script.Step(0);
            Assert.Equal(new ScriptMarker(new(1, 128 / 255d, 0), 16, .6) { Shape = ScriptMarkerShape.Diamond },
                session.Markers(0)[SolarSystemSample.EarthId]);
            Assert.Equal(ScriptMarkerShape.FilledSquare, session.Markers(0)[SolarSystemSample.MoonId].Shape);
            script.Step(1);
            Assert.Equal(SolarSystemSample.EarthId, Assert.Single(session.Markers(0)).Key);
            script.Step(1);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.Empty(session.Markers(0));
            Assert.Equal(2, session.Markers(1).Count);
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicReferenceFramesPreserveTheEyeAndDoNotChangeSelectionOrAnotherView()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        session.SelectObject(0, PlanetCatalogue.MarsId);
        var untouched = session[1];
        var snapshot = world.Graph.Evaluate(epoch);
        var start = session[0].Navigation!.Value;
        Vector3d eye = start.AnchorKilometers + start.ResolvePlacement(snapshot, session.DeltaT).OffsetKilometers;
        var folder = Directory.CreateTempSubdirectory("st-cel-frames-");
        try
        {
            string source = Path.Combine(folder.FullName, "frames.cel");
            File.WriteAllText(source, """
                {
                    setframe { coordsys "equatorial" ref "Sol/Earth" }
                    wait { duration 1 }
                    setframe { coordsys "bodyfixed" ref "Sol/Earth" }
                    wait { duration 1 }
                    setframe { coordsys "phaselock" ref "Sol/Earth" target "Sol/Earth/Moon" }
                    wait { duration 1 }
                    setframe { coordsys "chase" ref "Sol/Earth" }
                    wait { duration 1 }
                    setframe { coordsys "observer" ref "Sol/Earth" }
                    wait { duration 1 }
                    setframe { coordsys "universal" }
                    setframe { coordsys "unknown" }
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Equal(["line 13: setframe unknown"], conversion.Unapplied);
            string lua = Path.Combine(folder.FullName, "frames.lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            Check(0, NavigationFrame.Equatorial, SolarSystemSample.EarthId);
            Check(1, NavigationFrame.Inertial, SolarSystemSample.EarthId, bodyFixed: true);
            Check(1, NavigationFrame.PhaseLock, SolarSystemSample.EarthId, SolarSystemSample.MoonId);
            Check(1, NavigationFrame.Chase, SolarSystemSample.EarthId, SolarSystemSample.SunId);
            Check(1, NavigationFrame.Inertial, SolarSystemSample.EarthId);
            Check(1, NavigationFrame.Inertial, null);
            Assert.True(script.Finished);
            Assert.Null(script.Error);

            void Check(double seconds, NavigationFrame frame, ObjectId? target, ObjectId? reference = null, bool bodyFixed = false)
            {
                script.Step(seconds);
                var pose = session[0].Navigation!.Value;
                Assert.Equal(target, pose.Target);
                Assert.Equal(target.HasValue, pose.Tracking);
                Assert.Equal(frame, pose.Placement!.Value.Frame);
                Assert.Equal(bodyFixed, pose.Placement.Value.BodyFixed);
                Assert.Equal(reference, pose.Placement.Value.Reference);
                Assert.Equal(PlanetCatalogue.MarsId, session[0].Selection);
                Assert.InRange((pose.AnchorKilometers + pose.ResolvePlacement(snapshot, session.DeltaT).OffsetKilometers - eye).Length, 0, 1e-6);
                Assert.Equal(untouched, session[1]);
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicLookbackAndLayerFlagsKeepTheirEffectsInTheRunningView()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        var first = session[0] with { EarthObserver = EarthObserverCamera.Tromso, Playing = false };
        var second = session[1] with { Epoch = epoch.AddSeconds(3600), Playing = false };
        session.RestoreScenario(new([first, second], false, 0, 0, session.DeltaT));
        session.SelectObject(0, SolarSystemSample.EarthId);
        Assert.True(session.FollowBodyRotation(0));
        var before = session[0].Navigation!.Value;
        double delay = session.LightTimeSeconds(0)!.Value;
        var folder = Directory.CreateTempSubdirectory("st-cel-lookback-");
        try
        {
            string source = Path.Combine(folder.FullName, "lookback.cel");
            File.WriteAllText(source, """
                {
                    lookback {}
                    renderflags { set "horizontalgrid|lightdelay|globulars" }
                    wait { duration 1 }
                    renderflags { clear "horizontalgrid|lightdelay|globulars" }
                    lookback {}
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Empty(conversion.Unapplied);
            string lua = Path.Combine(folder.FullName, "lookback.lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            script.Step(0);
            var reversed = session[0];
            Assert.Equal(before with { Placement = before.Placement!.Value with { Forward = before.Placement.Value.Forward * -1 } },
                reversed.Navigation);
            Assert.Equal(EarthObserverCamera.Tromso with { HorizonGrid = true }, reversed.EarthObserver);
            Assert.True(reversed.Sky.DeepSky);
            Assert.True(reversed.LightTimeDelayed);
            Assert.Equal(epoch.AddSeconds(-delay), reversed.Epoch);
            Assert.Equal(second, session[1]);
            script.Step(1);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.Equal(before, session[0].Navigation);
            Assert.Equal(EarthObserverCamera.Tromso, session[0].EarthObserver);
            Assert.False(session[0].Sky.DeepSky);
            Assert.False(session[0].LightTimeDelayed);
            Assert.Equal(second, session[1]);
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicCameraPoseCommandsReuseArchiveCoordinatesAndKeepTheSecondView()
    {
        string sources = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        var other = session[1] with { Epoch = epoch.AddSeconds(3600), Playing = false };
        session.RestoreScenario(new([session[0] with { Playing = false }, other], false, 0, 0, session.DeltaT));
        var folder = Directory.CreateTempSubdirectory("st-cel-pose-");
        try
        {
            string original = File.ReadAllText(Path.Combine(sources, "gotoloc.cel"));
            string encoded = Regex.Match(original, @"x\s+""[^""]*""\s+y\s+""[^""]*""\s+z\s+""[^""]*""").Value;
            Assert.NotEmpty(encoded);
            string source = Path.Combine(folder.FullName, "pose.cel");
            File.WriteAllText(source, $$"""
                {
                    setposition { {{encoded}} }
                    wait { duration 1 }
                    setposition { base [0 0 0] offset [100 200 300] }
                    setorientation { axis [0 1 0] angle 90 }
                    wait { duration 1 }
                    setorientation { ow 1 ox 0 oy 0 oz 0 }
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Empty(conversion.Unapplied);
            string lua = Path.Combine(folder.FullName, "pose.lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            var sun = world.Ephemeris.Sample(epoch).Sun.PositionKilometers;
            script.Step(0);
            var encodedPose = session[0].Navigation!.Value;
            Assert.True(encodedPose.Tracking);
            Assert.InRange((Eye(encodedPose) - sun - new Vector3d(30988223546.628002, -70180951.86538383,
                -30417101.389447883)).Length, 0, .0001);
            script.Step(1);
            var numericPose = session[0].Navigation!.Value;
            var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
            Assert.InRange((Eye(numericPose) - sun - new Vector3d(100, -300 * cos - 200 * sin, 200 * cos - 300 * sin)).Length, 0, 1e-6);
            Assert.InRange((numericPose.Placement!.Value.Forward - new Vector3d(1, 0, 0)).Length, 0, 1e-12);
            script.Step(1);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.Equal(Eye(numericPose), Eye(session[0].Navigation!.Value));
            Assert.Equal(new Vector3d(0, cos, sin), session[0].Navigation!.Value.Placement!.Value.Forward);
            Assert.Equal(other, session[1]);

            Vector3d Eye(NavigationCamera pose) =>
                pose.AnchorKilometers + pose.ResolvePlacement(world.Graph.Evaluate(epoch), session.DeltaT).OffsetKilometers;
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicMoveUsesCameraAxesAndExitStopsOnlyTheTour()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        var other = session[1] with { Epoch = epoch.AddSeconds(3600), Playing = false };
        session.RestoreScenario(new([session[0] with { Playing = false }, other], false, 0, 0, session.DeltaT));
        var folder = Directory.CreateTempSubdirectory("st-cel-move-");
        try
        {
            string source = Path.Combine(folder.FullName, "move.cel");
            File.WriteAllText(source, """
                {
                    select { object "Earth" }
                    synchronous {}
                    move { velocity [12 20 -28] duration 2 }
                    exit {}
                    wait { duration 300 }
                    set { name "FOV" value 33 }
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Empty(conversion.Unapplied);
            Assert.Equal(4, conversion.Commands);
            string lua = Path.ChangeExtension(source, ".lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            var snapshot = world.Graph.Evaluate(epoch);
            script.Step(0);
            var before = session[0].Navigation!.Value;
            var basis = before.ResolvePlacement(snapshot, session.DeltaT);
            Vector3d velocity = Vector3d.Cross(basis.Forward, basis.Up) * 12 + basis.Up * 20 + basis.Forward * 28;
            script.Step(.375);
            Check(.375);
            script.Step(.625);
            Check(1);
            script.Step(2);
            Check(2);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            Assert.NotEqual(33, session[0].EffectiveFieldOfView);

            void Check(double seconds)
            {
                var pose = session[0].Navigation!.Value;
                var placement = pose.ResolvePlacement(snapshot, session.DeltaT);
                Vector3d traveled = (pose.AnchorKilometers - before.AnchorKilometers) + (placement.OffsetKilometers - basis.OffsetKilometers);
                Assert.InRange((traveled - velocity * seconds).Length, 0, 1e-6);
                Assert.Equal(before.Target, pose.Target);
                Assert.True(pose.Tracking);
                Assert.Equal(before.Placement!.Value.Frame, pose.Placement!.Value.Frame);
                Assert.InRange((placement.Forward - basis.Forward).Length, 0, 1e-12);
                Assert.Equal(other, session[1]);
            }
        }
        finally { folder.Delete(recursive: true); }
    }


    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicTrackKeepsTheEarthFrameWhileAimingAtTheMoon()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        var other = session[1] with { Epoch = epoch.AddSeconds(3600), Playing = false };
        session.RestoreScenario(new([session[0] with { Playing = false }, other], false, 0, 0, session.DeltaT));
        var folder = Directory.CreateTempSubdirectory("st-cel-track-");
        try
        {
            string source = Path.Combine(folder.FullName, "track.cel");
            File.WriteAllText(source, """
                {
                    select { object "Earth" }
                    synchronous {}
                    select { object "Moon" }
                    track {}
                    select { object "Mars" }
                }
                """);
            var conversion = TourConverter.ConvertFile(source);
            Assert.Empty(conversion.Unapplied);
            string lua = Path.ChangeExtension(source, ".lua");
            File.WriteAllText(lua, conversion.Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            script.Step(0);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            var initial = session[0].Navigation!.Value;
            CheckAim(initial);
            session.Step(0, 3600);
            CheckAim(initial);
            string saved = Path.Combine(folder.FullName, "track.stscene");
            ScenarioFile.Save(saved, session.CaptureScenario(0));
            session.RestoreScenario(ScenarioFile.Load(saved));
            Assert.True(session.TryReplaceWorld(new SimulationWorld(world.Ephemeris, [])));
            CheckAim(initial);

            File.WriteAllText(lua, "st.track(nil)");
            script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            script.Step(0);
            Assert.Null(script.Error);
            Assert.Null(session[0].ScriptAimTarget);

            void CheckAim(NavigationCamera expected)
            {
                var snapshot = world.Graph.Evaluate(session[0].Epoch);
                session.UpdateCamera(0, snapshot, 0);
                var camera = session[0].Navigation!.Value;
                Assert.Equal(SolarSystemSample.EarthId, camera.Target);
                Assert.Equal(SolarSystemSample.MoonId, session[0].ScriptAimTarget);
                Assert.Equal(PlanetCatalogue.MarsId, session[0].Selection);
                Assert.Equal(expected.Placement!.Value.Frame, camera.Placement!.Value.Frame);
                Assert.Equal(expected.Placement.Value.OffsetKilometers, camera.Placement.Value.OffsetKilometers);
                Assert.True(world.TryNavigationState(SolarSystemSample.MoonId, snapshot, out var target));
                var placement = camera.ResolvePlacement(snapshot, session.DeltaT);
                Vector3d sight = target.PositionKilometers - (camera.AnchorKilometers + placement.OffsetKilometers);
                Assert.InRange((placement.Forward - sight / sight.Length).Length, 0, 1e-12);
                Assert.Equal(other, session[1]);
            }
        }
        finally { folder.Delete(recursive: true); }
    }


    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ClassicTurnsIntegrateElapsedTimeAndPreserveTheRotatingFrame()
    {
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.OpenSpaceView(0);
        session.Split();
        session.Synchronize(false, 0);
        var other = session[1];
        var folder = Directory.CreateTempSubdirectory("st-cel-turn-");
        try
        {
            string source = Path.Combine(folder.FullName, "turn.cel");
            File.WriteAllText(source, """
                {
                    select { object "Earth" }
                    synchronous {}
                    rotate { axis [0 1 0] rate 40 duration 2 }
                    orbit { axis [0 1 0] rate 20 duration 1 }
                }
                """);
            string lua = Path.ChangeExtension(source, ".lua");
            File.WriteAllText(lua, TourConverter.ConvertFile(source).Lua);
            var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
            var snapshot = world.Graph.Evaluate(epoch);
            script.Step(0);
            var original = session[0].Navigation!.Value;
            var before = original.ResolvePlacement(snapshot, session.DeltaT);
            script.Step(.125);
            CheckTurn(5);
            script.Step(1.875);
            var rotated = CheckTurn(80);
            script.Step(.4);
            script.Step(.6);
            Assert.True(script.Finished);
            Assert.Null(script.Error);
            var orbit = CheckTurn(100, keepsEye: false);
            Assert.Equal(rotated.OffsetKilometers.Length, orbit.OffsetKilometers.Length, 6);
            Assert.True((rotated.OffsetKilometers - orbit.OffsetKilometers).Length > 1);
            Assert.Equal(other, session[1]);

            NavigationPlacement CheckTurn(double degrees, bool keepsEye = true)
            {
                var pose = session[0].Navigation!.Value;
                Assert.Equal(original.Target, pose.Target);
                Assert.Equal(original.Placement!.Value.Frame, pose.Placement!.Value.Frame);
                var placement = pose.ResolvePlacement(snapshot, session.DeltaT);
                if (keepsEye)
                    Assert.InRange((placement.OffsetKilometers - before.OffsetKilometers).Length, 0, 1e-6);
                Assert.Equal(Math.Cos(degrees * Math.PI / 180), Vector3d.Dot(before.Forward, placement.Forward), 11);
                return placement;
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    [TourArchiveFact]
    [Trait("Category", "ExternalData")]
    public void OriginalDemosReportTheLegacyRateLimitAndUpdateSynchronizedViews()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.Split();
        var folder = Directory.CreateTempSubdirectory("st-cel-rate-");
        try
        {
            foreach (string name in new[] { "demo.cel", "demo_ru.cel" })
            {
                string command = Assert.Single(File.ReadLines(Path.Combine(root, name)),
                    line => line.Contains("rate 2592000", StringComparison.Ordinal));
                Run(command);
                Assert.True(session[0].Playing && session[1].Playing);
                Assert.Equal(1_000_000, session[0].PlaybackRate);
                Assert.Equal(1_000_000, session[1].PlaybackRate);
                session.Tick(.5);
                Assert.Equal(session[0].Epoch, session[1].Epoch);
            }

            session.Synchronize(false, 0);
            var untouched = session[1];
            Run("timerate { rate -2592000 }");
            Assert.Equal(-1_000_000, session[0].PlaybackRate);
            Assert.Equal(untouched, session[1]);

            void Run(string command)
            {
                string source = Path.Combine(folder.FullName, "rate.cel");
                File.WriteAllText(source, "{ " + command.Trim() + " }");
                var conversion = TourConverter.ConvertFile(source);
                Assert.EndsWith(" limited to " + (command.Contains('-') ? "-" : "") + "1000000", Assert.Single(conversion.Unapplied));
                string lua = Path.ChangeExtension(source, ".lua");
                File.WriteAllText(lua, conversion.Lua);
                var script = LuaSessionScript.Load(lua, session, 0, false, _ => { });
                script.Step(0);
                Assert.True(script.Finished);
                Assert.Null(script.Error);
            }
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class TourArchiveFactAttribute : FactAttribute
    {
        public TourArchiveFactAttribute()
        {
            if (new[] { "SPACETANGENT_TOUR_SOURCES", "SPACETANGENT_DE440" }
                .Any(key => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))))
                Skip = "Set the original tour and ephemeris paths.";
        }
    }
}
