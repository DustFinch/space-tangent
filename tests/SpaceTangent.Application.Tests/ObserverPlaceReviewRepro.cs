// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

/// <summary>Regression contracts for recording and sharing workspaces with private place history.</summary>
public sealed class ObserverPlaceReviewRepro
{
    private static readonly JulianInstant J2000 = new(2451545, 0, TimeScale.Tdb);
    private static readonly EarthObserverCamera Home = EarthObserverCamera.Default with { Site = new(-33.9, 18.4, .02) };
    private static readonly EarthObserverCamera Moon = new(new(12, 34, .1), 25, 30) { Body = SolarSystemSample.MoonId };

    // 1. Every live view has a place identity, so every recorded frame is a v52 scene (up to 256 KiB). The reader still
    //    caps a frame at the old 32 KiB: the recording is written successfully but cannot be replayed.
    [Fact]
    public void ARecordingOfALargeWorkspaceReplays()
    {
        var scenario = LargeWorkspace();
        Assert.True(ScenarioFile.Encode(scenario).Length > 32 * 1024, "The fixture must exceed the pre-v52 frame size.");
        using var stream = new MemoryStream();
        using (var writer = new ObserverFrameSequence.Writer(stream, 30))
        {
            writer.Append(scenario);
            writer.Complete();
        }
        stream.Position = 0;
        using var reader = new ObserverFrameSequence.Reader(stream);
        var frame = reader.ReadNext()!;
        Assert.Equal(scenario.Views, frame.Views);
        Assert.Equal(scenario.Views.Select(view => view.Places.ViewKey), frame.Views.Select(view => view.Places.ViewKey));
        Assert.Null(reader.ReadNext());
    }

    // 2. SceneLink.Write has no length check and Read rejects links above 44,000 characters. A written link must either
    //    open again or be refused when it is created, never handed to the user unreadable.
    [Fact]
    public void ACreatedSceneLinkCanBeOpened()
    {
        string link;
        try { link = SceneLink.Write(LargeWorkspace()); }
        catch (InvalidDataException) { return; }
        Assert.True(link.Length <= SceneLink.MaximumLength);
        Assert.Null(Record.Exception(() => SceneLink.Read(link, null!, DeltaTModel.FixedOffset)));
    }

    // 3. A shared link should describe the scene, not the sender's other remembered places: a view on the Moon must not
    //    carry the sender's home coordinates on Earth. Scene files and preferences may keep the full history.
    [Fact]
    public void ASharedLinkDoesNotPublishInactivePlaces()
    {
        var view = new ObserverView(J2000, CameraMode.Surface, null, false)
        {
            EarthObserver = Moon,
            Places = ObserverPlaceMemory.FromSaved(Guid.NewGuid(), [Home, Moon])
        };
        var link = SceneLink.Write(new([view], false, 0, 0, DeltaTModel.FixedOffset));
        var opened = Assert.Single(SceneLink.Read(link, null!, DeltaTModel.FixedOffset).Scenario.Views);
        Assert.Equal(Moon, opened.EarthObserver);
        Assert.Null(opened.Places.Find(SolarSystemSample.EarthId));
        Assert.False(opened.Places.HasIdentity);
        Assert.Equal(Home, view.Places.Find(SolarSystemSample.EarthId));
        Assert.Equal(view, Assert.Single(ScenarioFile.Decode(ScenarioFile.Encode(
            new([view], false, 0, 0, DeltaTModel.FixedOffset))).Views));
    }

    [Fact]
    public void SharingAnImplicitSurfaceCameraPreservesItsEffectivePlace()
    {
        var view = new ObserverView(J2000, CameraMode.Surface, null, false)
        {
            Places = ObserverPlaceMemory.FromSaved(Guid.NewGuid(), [Home])
        };
        var opened = Assert.Single(SceneLink.Read(SceneLink.Write(new([view], false, 0, 0,
            DeltaTModel.FixedOffset)), null!, DeltaTModel.FixedOffset).Scenario.Views);
        Assert.Equal(view.ObserverCamera, opened.ObserverCamera);
        Assert.Empty(opened.Places.Locations);
    }

    [Fact]
    public void AnOversizedBookmarkReportsItsSizeWithoutReplacingTheFile()
    {
        var root = Directory.CreateTempSubdirectory("st-bookmark-size-");
        try
        {
            string path = Path.Combine(root.FullName, "bookmarks.json");
            var file = new ObserverBookmarkFile(path);
            file.Save([]);
            byte[] before = File.ReadAllBytes(path);
            var error = Assert.Throws<InvalidDataException>(() => file.Save([
                new("large", SceneLink.Scheme + new string('A', SceneLink.MaximumLength))]));
            Assert.Contains("size", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { root.Delete(true); }
    }

    // Sixteen views, each remembering Earth and five other bodies: about 55 KB as a v52 scene.
    private static SimulationScenario LargeWorkspace()
    {
        var views = Enumerable.Range(0, 16).Select(index =>
        {
            var places = new List<EarthObserverCamera> { Home with { AzimuthDegrees = index } };
            for (int body = 0; body < 5; body++)
                places.Add(Moon with { Body = new($"review/body-{body}"), AzimuthDegrees = index + body });
            return new ObserverView(J2000, CameraMode.Surface, null, false)
            {
                EarthObserver = places[0],
                Places = ObserverPlaceMemory.FromSaved(Guid.NewGuid(), places)
            };
        }).ToArray();
        return new(views, false, 0, 0, DeltaTModel.FixedOffset) { Layout = Tree(0, views.Length) };
    }

    private static ObserverLayout Tree(int first, int count) => count == 1 ? new(first) :
        new(null, ViewSplit.SideBySide, .5, Tree(first, count / 2), Tree(first + count / 2, count - count / 2));
}
