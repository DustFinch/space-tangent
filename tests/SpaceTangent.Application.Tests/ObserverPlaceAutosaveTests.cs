// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverPlaceAutosaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spacetangent-places-" + Guid.NewGuid().ToString("N"));
    public ObserverPlaceAutosaveTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string FilePath => Path.Combine(_root, "places.json");

    [Fact]
    public void DebounceCoalescesChangesAndFlushWritesTheLatestPlacesForBothViews()
    {
        var session = SharedClockTests.Create(); var store = new ObserverPlaceAutosave(session, FilePath);
        Assert.Equal(0, store.Restore(false)); store.Advance(1); Assert.False(File.Exists(FilePath));
        session.SetEarthObserver(0, EarthObserverCamera.Greenwich with { AzimuthDegrees = 10 });
        store.Advance(.25); Assert.False(File.Exists(FilePath));
        session.Split(); session.SetEarthObserver(1, EarthObserverCamera.Tromso);
        store.Advance(.25); Assert.False(File.Exists(FilePath));
        store.Advance(.25); Assert.True(File.Exists(FilePath));
        byte[] saved = File.ReadAllBytes(FilePath);
        Assert.Equal(ObserverPlacePreferences.Capture(session), saved);
        store.Advance(100); Assert.Equal(saved, File.ReadAllBytes(FilePath));
        session.SetEarthObserver(0, EarthObserverCamera.Greenwich with { AzimuthDegrees = 90 });
        store.Flush(); Assert.Equal(ObserverPlacePreferences.Capture(session), File.ReadAllBytes(FilePath));
        var restored = SharedClockTests.Create();
        Assert.Equal(1, new ObserverPlaceAutosave(restored, FilePath).Restore(true));
        Assert.Equal(session[0].ObserverCamera, restored[0].ObserverCamera);
    }

    [Fact]
    public void FailedWritesRetainDirtyStateAndRetryAfterFiveSeconds()
    {
        string blocker = Path.Combine(_root, "not-a-directory"); File.WriteAllText(blocker, "blocked");
        string target = Path.Combine(blocker, "places.json");
        var session = SharedClockTests.Create(); var store = new ObserverPlaceAutosave(session, target);
        store.Restore(false); session.SetEarthObserver(0, EarthObserverCamera.Tromso);
        Assert.ThrowsAny<IOException>(() => store.Advance(.5));
        File.Delete(blocker); Directory.CreateDirectory(blocker);
        store.Advance(4); Assert.False(File.Exists(target));
        store.Advance(1); Assert.Equal(ObserverPlacePreferences.Capture(session), File.ReadAllBytes(target));
    }

    [Fact]
    public void RejectedSavedPlacesAreNotOverwrittenUntilTheObserverChanges()
    {
        File.WriteAllText(FilePath, "invalid");
        var session = SharedClockTests.Create(); session.SetEarthObserver(0, EarthObserverCamera.Tromso);
        var store = new ObserverPlaceAutosave(session, FilePath);
        Assert.Throws<InvalidDataException>(() => store.Restore(false));
        store.Advance(100); store.Flush(); Assert.Equal("invalid", File.ReadAllText(FilePath));
        session.SetEarthObserver(0, EarthObserverCamera.Greenwich); store.Flush();
        Assert.Equal(ObserverPlacePreferences.Capture(session), File.ReadAllBytes(FilePath));
    }
}
