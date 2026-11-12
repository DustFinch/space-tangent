// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LocalSkySurveyTests
{
    [SurveyFact]
    [Trait("Category", "ExternalData")]
    public void OriginalDssMetadataAndCachedTilesResolveTheOrionField()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SKY_SURVEYS")!;
        var survey = LocalSkySurvey.Read(Path.Combine(root, "dss"));
        Assert.Equal("DSS colored", survey.Title);
        Assert.Equal((9, 512, "jpg"), (survey.MaximumOrder, survey.TileWidth, survey.Extension));
        Assert.Equal(Basis3d.Identity, survey.ToEquatorial);
        Assert.True(File.Exists(survey.AllSkyPath));
        double ra = 83.82 * Math.PI / 180, dec = -5.39 * Math.PI / 180;
        var direction = new Vector3d(Math.Cos(ra) * Math.Cos(dec), Math.Sin(ra) * Math.Cos(dec), Math.Sin(dec));
        long cell = HealpixSkyGrid.CellAt(5, direction);
        Assert.Equal(5359, cell);
        Assert.True(File.Exists(survey.TilePath(5, cell)));
        var (u, v) = LocalSkySurvey.TileUv(6, cell * 4 + 2, 5, 512, 0, 0);
        Assert.Equal(.5, u);
        Assert.Equal(.5 / 512, v);
        Assert.Equal(((64 + .5) / 1728, (64 + .5) / 1856), LocalSkySurvey.AllSkyUv(3, 28, 1728, 1856, 0, 0));
    }

    [Fact]
    public void PersistsSurveySelectionPerObserverAndRejectsOutOfRangeBrightness()
    {
        var first = new ObserverView(new(2451545, 0, TimeScale.Tdb), CameraMode.Solar, null, false);
        var surveyed = first with { Sky = first.Sky with { SurveyId = "dss", SurveyBrightness = .6 } };
        var other = first with { Epoch = first.Epoch.AddSeconds(3600) };
        var scene = new SimulationScenario([surveyed, other], false, 0, 0, DeltaTModel.MorrisonStephenson2004);
        string path = Path.GetTempFileName();
        try
        {
            ScenarioFile.Save(path, scene);
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
            Assert.Throws<InvalidDataException>(() => ScenarioFile.Save(path, scene with
            {
                Views = [surveyed with { Sky = surveyed.Sky with { SurveyBrightness = 3 } }]
            }));
            Assert.Equal(scene.Views, ScenarioFile.Load(path).Views);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class SurveyFactAttribute : FactAttribute
    {
        public SurveyFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SKY_SURVEYS")))
                Skip = "Set the archived survey root.";
        }
    }
}
