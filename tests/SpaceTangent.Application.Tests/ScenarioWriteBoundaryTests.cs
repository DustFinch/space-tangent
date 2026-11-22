// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ScenarioWriteBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-scenario-write-");

    [Theory]
    [InlineData("epoch")]
    [InlineData("navigation")]
    [InlineData("observer")]
    [InlineData("field")]
    [InlineData("rate")]
    [InlineData("layout")]
    [InlineData("marks")]
    [InlineData("optics")]
    [InlineData("sky")]
    [InlineData("auto-orbit")]
    [InlineData("sensitivity")]
    [InlineData("negative-light-delay")]
    [InlineData("inactive-light-delay")]
    public void InvalidViewStateCannotOverwriteAnExistingScenario(string fault)
    {
        var session = SharedClockTests.Create(); var scenario = session.CaptureScenario(0); var view = scenario.Views[0];
        var bad = fault == "layout" ? scenario with { SplitLayout = (ViewSplit)999 } : scenario with { Views = [fault switch
        {
            "epoch" => view with { Epoch = new(2451545, 0, TimeScale.Tt) },
            "navigation" => view with { Navigation = new(default, 0, 0, 0, null, false) },
            "observer" => view with { EarthObserver = EarthObserverCamera.Moscow with { Site = new(91, 0, 0) } },
            "field" => view with { FieldOfViewDegrees = 1 },
            "rate" => view with { PlaybackRate = double.NaN },
            "marks" => view with { ReferenceMarks = (BodyReferenceMarks)(1 << 30) },
            "optics" => view with { Optics = new(ObserverOpticsLibrary.Default with { Barlow = 0 }, true) },
            "sky" => view with { Sky = view.Sky with { CultureId = "../outside" } },
            "auto-orbit" => view with { AutoOrbit = new(1, false) },
            "sensitivity" => view with { MouseSensitivity = 0 },
            "negative-light-delay" => view with { LightTimeDelayed = true, LightTimeOffsetNanoseconds = -1 },
            _ => view with { LightTimeDelayed = false, LightTimeOffsetNanoseconds = 1 }
        }] };
        string path = Path.Combine(_root.FullName, "scene.stscene");
        ScenarioFile.Save(path, scenario); byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Save(path, bad));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(_root.FullName, ".scene-*.tmp"));
    }

    [Fact]
    public void FailureToReplaceTheDestinationCleansTheDurableTemporaryFile()
    {
        string path = Path.Combine(_root.FullName, "blocked"); Directory.CreateDirectory(path);
        var error = Record.Exception(() => ScenarioFile.Save(path, SharedClockTests.Create().CaptureScenario(0)));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.True(Directory.Exists(path)); Assert.Empty(Directory.GetFiles(_root.FullName, ".scene-*.tmp"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1024 * 1024 + 1)]
    public void EmptyAndOversizedScenarioFilesAreRejectedBeforeDeserialization(int size)
    {
        string path = Path.Combine(_root.FullName, "scene.stscene");
        using (var stream = File.Create(path)) stream.SetLength(size);
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Load(path));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
