// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class WorkspacePersistenceOwnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spacetangent-workspace-" + Guid.NewGuid().ToString("N"));
    public WorkspacePersistenceOwnerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NativeScenarioReadingKeepsItsOwnExactStateAndRemainsLazyUntilRequested()
    {
        string path = Path.Combine(_root, "observer.stscene");
        using var input = new ObserverWorkspaceReader(path);
        Assert.False(input.IsFavorites);
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0); session.Step(1, 123.456);
        var original = session.CaptureScenario(1); ScenarioFile.Save(path, original);
        var restored = input.ReadScenario(session.World, DeltaTModel.FixedOffset);
        Assert.Equal(ScenarioFile.Encode(original), ScenarioFile.Encode(restored));
        input.Dispose(); input.Dispose();
        Assert.Throws<ObjectDisposedException>(() => input.ReadScenario(session.World, session.DeltaT));
    }

    [Fact]
    public void ArchivedCollectionOwnsItsStreamAndReleasesItAtDispose()
    {
        string path = Path.Combine(_root, "favorites.JSON"); File.WriteAllText(path, "  []");
        var session = SharedClockTests.Create();
        using (var input = new ObserverWorkspaceReader(path))
        {
            Assert.True(input.IsFavorites);
            Assert.Throws<InvalidOperationException>(() => input.ReadScenario(session.World, session.DeltaT));
            Assert.ThrowsAny<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public void FailedClassificationClosesThePartiallyConstructedReader()
    {
        string path = Path.Combine(_root, "oversized.json");
        using (var file = File.Create(path)) file.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => new ObserverWorkspaceReader(path));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("morrisonstephenson2004")]
    [InlineData("-1")]
    public void InvalidPreferenceNamesKeepTheExistingDefault(string value)
    {
        string path = Path.Combine(_root, "delta-t.txt"); File.WriteAllText(path, value);
        Assert.Equal(DeltaTModel.EspenakMeeus2006, DeltaTPreferenceFile.Load(path));
    }

    [Fact]
    public void PreferencesRetainTheirNamedFormatAndCleanTemporaryFilesAfterRejection()
    {
        string path = Path.Combine(_root, "delta-t.txt");
        Assert.Equal(DeltaTModel.EspenakMeeus2006, DeltaTPreferenceFile.Load(path));
        DeltaTPreferenceFile.Save(path, DeltaTModel.MorrisonStephenson2004);
        Assert.Equal("MorrisonStephenson2004", File.ReadAllText(path));
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, DeltaTPreferenceFile.Load(path));
        File.Delete(path); Directory.CreateDirectory(path);
        var failure = Record.Exception(() => DeltaTPreferenceFile.Save(path, DeltaTModel.FixedOffset));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
