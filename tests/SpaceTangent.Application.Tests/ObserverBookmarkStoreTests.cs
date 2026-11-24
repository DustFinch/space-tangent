// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverBookmarkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spacetangent-bookmarks-" + Guid.NewGuid().ToString("N"));
    public ObserverBookmarkStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private string FilePath => Path.Combine(_root, "bookmarks.json");

    [Fact]
    public void ReplacingAndDeletingBookmarksPreservesOrderAndSelectionAcrossReload()
    {
        var store = new ObserverBookmarkStore(FilePath); store.Load();
        var session = SharedClockTests.Create();
        Assert.Equal(0, store.Store("First", session.CaptureScenario(0)));
        Assert.Equal(1, store.Store("Second", session.CaptureScenario(0)));
        session.SetEarthObserver(0, EarthObserverCamera.Tromso);
        Assert.Equal(1, store.Store(" FIRST ", session.CaptureScenario(0)));
        Assert.Equal(new[] { "Second", "FIRST" }, store.Items.Select(item => item.Name));
        var loaded = new ObserverBookmarkStore(FilePath); loaded.Load();
        Assert.Equal(store.Items, loaded.Items);
        Assert.Equal(0, loaded.Delete(1)); Assert.Equal("Second", Assert.Single(loaded.Items).Name);
        Assert.Equal(-1, loaded.Delete(0)); Assert.Empty(loaded.Items);
        loaded.Load(); Assert.Empty(loaded.Items);
    }

    [Fact]
    public void ValidationAndDiskFailuresKeepTheLastPublishedCollection()
    {
        var store = new ObserverBookmarkStore(FilePath);
        var scenario = SharedClockTests.Create().CaptureScenario(0);
        store.Store("Keep", scenario);
        var before = store.Items; byte[] bytes = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidDataException>(() => store.Store(" ", scenario));
        Assert.Same(before, store.Items); Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        File.Delete(FilePath); Directory.CreateDirectory(FilePath);
        var failure = Record.Exception(() => store.Store("Rejected", scenario));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Same(before, store.Items);
    }

    [Fact]
    public void EmptyAndRejectedImportsKeepTheExistingSelectionAndEntries()
    {
        var store = new ObserverBookmarkStore(FilePath); var session = SharedClockTests.Create();
        store.Store("First", session.CaptureScenario(0)); store.Store("Second", session.CaptureScenario(0));
        string input = Path.Combine(_root, "favorites.json"); File.WriteAllText(input, "[]");
        var result = store.ImportFavorites(input, session.World, session.DeltaT, 1);
        Assert.Equal((1, 0, 0), result); Assert.Equal(2, store.Items.Count);
        var before = store.Items; File.WriteAllText(input, "{}");
        Assert.Throws<InvalidDataException>(() => store.ImportFavorites(input, session.World, session.DeltaT, 1));
        Assert.Same(before, store.Items);
    }
}
