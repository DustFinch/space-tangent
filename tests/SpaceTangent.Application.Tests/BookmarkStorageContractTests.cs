// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Application;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class BookmarkStorageContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-bookmark-contract-");
    private string PathOf => Path.Combine(_root.FullName, "nested", "bookmarks.json");
    private ObserverBookmarkFile Store => new(PathOf);
    private static ObserverBookmark Entry(string name = "Scene", string? link = null) =>
        new(name, link ?? SceneLink.Scheme + "opaque-payload");

    [Fact]
    public void MissingBookmarkFileLoadsAnEmptyList()
    {
        Assert.Empty(Store.Load());
        Assert.False(Directory.Exists(Path.GetDirectoryName(PathOf)));
    }

    [Fact]
    public void SaveCreatesTheDirectoryAndRetainsUnicodeNames()
    {
        var entries = new[] { Entry("Луна — обзор"), Entry("Jason-1") };
        Store.Save(entries);
        Assert.Equal(entries, Store.Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(PathOf)!));
    }

    [Fact]
    public void EmptyListReplacesThePreviousCollection()
    {
        Store.Save([Entry()]);
        Store.Save([]);
        Assert.Empty(Store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("line\nbreak")]
    public void InvalidNameDoesNotReplaceThePreviousFile(string name)
    {
        Store.Save([Entry()]);
        byte[] before = File.ReadAllBytes(PathOf);
        Assert.Throws<InvalidDataException>(() => Store.Save([Entry(name)]));
        Assert.Equal(before, File.ReadAllBytes(PathOf));
    }

    [Fact]
    public void NameLengthBoundaryIsEnforced()
    {
        Store.Save([Entry(new string('a', 100))]);
        Assert.Equal(100, Assert.Single(Store.Load()).Name.Length);
        Assert.Throws<InvalidDataException>(() => Store.Save([Entry(new string('a', 101))]));
    }

    [Fact]
    public void CaseInsensitiveDuplicateNamesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Store.Save([Entry("Moon"), Entry("MOON")]));
        Assert.False(File.Exists(PathOf));
    }

    [Fact]
    public void UnsupportedLinkSchemeIsRejectedBeforeCreatingAFile()
    {
        Assert.Throws<InvalidDataException>(() => Store.Save([Entry(link: "https://example.invalid/scene")]));
        Assert.False(File.Exists(PathOf));
    }

    [Fact]
    public void BookmarkCountLimitIsEnforced()
    {
        var entries = Enumerable.Range(0, 257).Select(index => Entry("Scene " + index)).ToArray();
        Assert.Throws<InvalidDataException>(() => Store.Save(entries));
        Assert.False(File.Exists(PathOf));
    }

    [Fact]
    public void OversizedInputIsRejectedBeforeJsonParsing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        using (var stream = File.Create(PathOf)) stream.SetLength(4 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => Store.Load());
    }

    [Fact]
    public void JsonNullIsNotInterpretedAsAnEmptyValidCollection()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, "null");
        Assert.Throws<InvalidDataException>(() => Store.Load());
    }

    [Fact]
    public void CollectionProbeRecognizesWhitespaceAndLeavesTheStreamOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(" \n\t[]"));
        Assert.True(ObserverBookmarkFile.IsArchivedCollection(stream));
        Assert.True(stream.CanRead);
        using var objectStream = new MemoryStream("{}"u8.ToArray());
        Assert.False(ObserverBookmarkFile.IsArchivedCollection(objectStream));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
