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
