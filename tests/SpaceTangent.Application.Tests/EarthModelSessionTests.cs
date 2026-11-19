// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class EarthModelSessionTests
{
    [Fact]
    public void HistoricalEarthFixedViewsKeepTheirModelsAndEpochsAfterReload()
    {
        var root = Directory.CreateTempSubdirectory("st-earth-session-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "station.ssc"), """
                "Surface station" "Sol/Earth" {
                    OrbitFrame { BodyFixed { Center "Sol/Earth" } }
                    FixedPosition { Rectangular [6378.1366 0 0] }
                }
                """);
            SimulationWorld Load() => new(new StaticEphemeris(), [], bodyCatalog: SscBodyCatalogue.Load(root.FullName));
            var world = Load();
            var epoch = JulianInstant.FromJulianDate(2106215, TimeScale.Tdb);
            var session = new SimulationSession(world, epoch, DeltaTModel.FixedOffset);
            session.Split();
            session.Synchronize(false, 0);
            session.Step(1, 3600);
            var before = session.CaptureScenario(1);
            var station = Assert.Single(world.SscBodies).Id;
            Check(session);
            session.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            Check(session);
            session.SetDeltaT(DeltaTModel.FixedOffset);
            Assert.True(session.TryReplaceWorld(Load()));
            var after = session.CaptureScenario(1);
            Assert.Equal(before.Views.ToArray(), after.Views.ToArray());
            Assert.Equal(before.DeltaT, after.DeltaT);
            Assert.Equal(before.Synchronized, after.Synchronized);
            Assert.Equal(before.ClockOwner, after.ClockOwner);
            Assert.Equal(before.ActiveView, after.ActiveView);
            Assert.Equal(before.SplitLayout, after.SplitLayout);
            Check(session);

            void Check(SimulationSession state)
            {
                for (int view = 0; view < state.ViewCount; view++)
                {
                    var snapshot = state.World.Graph.Evaluate(state[view].Epoch, state.DeltaT);
                    Assert.True(snapshot.TryState(station, out var position));
                    Assert.True(snapshot.TryState(SolarSystemSample.EarthId, out var earth));
                    var expected = EarthOrientation.TerrestrialToWorld(state[view].Epoch, state.DeltaT).ToWorld.X * 6378.1366;
                    Assert.InRange(((position-earth).PositionKilometers-expected).Length, 0, 1e-8);
                }
            }
        }
        finally { root.Delete(true); }
    }

    private sealed class StaticEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}
