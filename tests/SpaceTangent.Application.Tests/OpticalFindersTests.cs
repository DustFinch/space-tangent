// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class OpticalFindersTests
{
    [Fact]
    public void PreservesIndependentFinderGeometryAndReadsArchivedSensorIndices()
    {
        var observer = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Surface, null, false);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"DeltaT":"MorrisonStephenson2004","Telrad":true,"Camera":4,"FinderCircle":true}""");
            var imported = LegacyObserverProfiles.Read(path, observer, DeltaTModel.MorrisonStephenson2004).Observer;
            var optics = Assert.IsType<ObserverOptics>(imported.Optics);
            var finder = Assert.IsType<OpticalFinders>(optics.Finders);
            Assert.Equal(new(true, CameraSensorPreset.Asi2600, true), finder);
            Assert.Equal(("ASI2600", 23.5, 15.7), finder.SensorDimensions);
            var (width, height) = finder.SensorFieldDegrees(optics.Profile with { Barlow = 2 });
            Assert.InRange(Math.Abs(Math.Tan(width * Math.PI / 360) - 23.5 / 4800), 0, 1e-12);
            Assert.InRange(Math.Abs(Math.Tan(height * Math.PI / 360) - 15.7 / 4800), 0, 1e-12);
            Assert.InRange(Math.Abs(OpticalFinders.HalfHeightFraction(2, 12) - Math.Tan(Math.PI / 180) / Math.Tan(Math.PI / 30)), 0, 1e-12);

            var scene = new SimulationScenario([imported, observer], false, 0, 0, DeltaTModel.MorrisonStephenson2004);
            ScenarioFile.Save(path, scene);
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(18, json["version"]!.GetValue<int>());
            json["version"] = 17;
            File.WriteAllText(path, json.ToJsonString());
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
