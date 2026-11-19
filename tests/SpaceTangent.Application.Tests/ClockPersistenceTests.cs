// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ClockPersistenceTests
{
    [Fact]
    public void LightTimeUndoSurvivesSplitSaveReloadAndChangedSelection()
    {
        var world = new SimulationWorld(new FixedEphemeris(), []);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(world, epoch, DeltaTModel.MorrisonStephenson2004);
        session.SelectObject(0, SolarSystemSample.MoonId);
        Assert.True(session.TrySetLightTimeDelay(0, true, out _));
        long offset = Assert.IsType<long>(session[0].LightTimeOffsetNanoseconds);
        session.Split();
        session.Synchronize(false, 0);
        session.Step(1, 3600);
        byte[] saved = ScenarioFile.Encode(session.CaptureScenario(1));
        session.RestoreScenario(ScenarioFile.Decode(saved));
        Assert.True(session.TryReplaceWorld(new(new FixedEphemeris(), [])));
        session.SelectObject(1, SolarSystemSample.EarthId);
        Assert.True(session.TrySetLightTimeDelay(1, false, out _));
        Assert.Equal(epoch.AddSeconds(3600), session[1].Epoch);
        Assert.True(session[0].LightTimeDelayed);
        session.Synchronize(true, 0);
        Assert.Equal(offset, session[1].LightTimeOffsetNanoseconds);
        Assert.True(session.TrySetLightTimeDelay(1, false, out _));
        Assert.All(session.CaptureScenario(0).Views, view =>
        {
            Assert.Equal(epoch, view.Epoch);
            Assert.False(view.LightTimeDelayed);
            Assert.Null(view.LightTimeOffsetNanoseconds);
        });
        // Older scenes keep their legacy measured-forward fallback; a newer offset cannot be smuggled into v10.
        string json = Encoding.UTF8.GetString(saved);
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(json.Replace("\"version\": 50", "\"version\": 10"))));
    }

    [Fact]
    public void PluginMemoryQuotaDoesNotDependOnElapsedTimeOrRunUntrustedCleanup()
    {
        var root = Directory.CreateTempSubdirectory("st-plugin-heap-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "main.lua"), """
                local retained = {}
                plugin.on("disable", function() print("cleanup ran") end)
                plugin.on("tick", function()
                    retained[#retained + 1] = string.rep(tostring(#retained), 262144)
                end)
                """);
            var session = new SimulationSession(new(new FixedEphemeris(), []),
                JulianInstant.FromJulianDate(2451545, TimeScale.Tdb), DeltaTModel.FixedOffset);
            var messages = new List<string>();
            var plugin = new ObserverLuaPlugin(new(root.FullName, "heap", "Heap", "1", "main.lua", []),
                session, 0, false, messages.Add);
            for (int tick = 0; tick < 512 && plugin.Enabled; tick++)
                plugin.Step(0);
            Assert.False(plugin.Enabled);
            Assert.Contains("retained memory quota", plugin.Error);
            Assert.DoesNotContain(messages, message => message.Contains("cleanup ran", StringComparison.Ordinal));
        }
        finally { root.Delete(true); }
    }

    private sealed class FixedEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}
