// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Addons;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;
using static SpaceTangent.Application.Addons.AddonPreparation;

namespace SpaceTangent.Application.Tests;

public sealed class AddonPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spacetangent-prepare-" + Guid.NewGuid().ToString("N"));
    public AddonPreparationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AddonSourcePath PathFor(string name, bool directory = false) =>
        new(null, Path.Combine(_root, name), Path.Combine(_root, "export", name), Path.Combine(_root, "user", name), directory);

    private static string ReadTrackedText(string file)
    {
        using var reader = new StreamReader(AddonDependencies.OpenRead(file, 64));
        return reader.ReadToEnd();
    }

    private AddonPreparationInputs Inputs() => new([], [], null, null, null,
        PathFor("planets", true), PathFor("frames.tpc"), PathFor("stars.tsv"), PathFor("archive", true),
        PathFor("stc", true), PathFor("corrections", true), PathFor("exoplanets.txt"), PathFor("locations", true),
        PathFor("deep-sky", true), PathFor("dust.bin"), PathFor("missions", true), PathFor("tle", true),
        Path.Combine(_root, "updates"), PathFor("sites.txt"), PathFor("saved-sites.tsv"), false, []);

    [Fact]
    public void UnchangedPreparationReusesTheExactWorldAndDecodedGenerationWithoutGodot()
    {
        var reader = new AddonPreparation();
        var ephemeris = new SharedClockTests.StaticEphemeris();
        var inputs = Inputs();
        var original = reader.Prepare(ephemeris, null, inputs, null, default);
        var again = reader.Prepare(ephemeris, original, inputs, null, default);
        Assert.Same(original, again);
        Assert.Same(original.World, again.World);
        Assert.Empty(original.Science);
        Assert.Empty(original.World.Satellites);
    }

    [Fact]
    public void ACancelledPreparationCannotPublishACachedGeneration()
    {
        var reader = new AddonPreparation(); var inputs = Inputs();
        var ephemeris = new SharedClockTests.StaticEphemeris();
        var original = reader.Prepare(ephemeris, null, inputs, null, default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Prepare(ephemeris, original, inputs, null, cancellation.Token));
        Assert.NotNull(original.World.Graph.Evaluate(original.World.Ephemeris.Start));
    }

    [Fact]
    public void CacheDetectsChangedContentEvenWithTheSameLengthAndTimestamp()
    {
        var path = PathFor("input.txt"); File.WriteAllText(path.Project, "first");
        DateTime stamp = File.GetLastWriteTimeUtc(path.Project);
        Cached<string> Read(Cached<string>? previous) => Cached<string>.Load(previous, path, "Fixture",
            (location, _) => ReadTrackedText(location.Resolve()), default);
        var original = Read(null); Assert.Same(original, Read(original));
        File.WriteAllText(path.Project, "other"); File.SetLastWriteTimeUtc(path.Project, stamp);
        var changed = Read(original);
        Assert.NotSame(original, changed); Assert.Equal("other", changed.Value); Assert.Equal("first", original.Value);
    }

    [Fact]
    public void CacheTracksNegativeLookupsAndRecoversAfterARejectedRead()
    {
        var path = PathFor("later.txt");
        Cached<string> Read(Cached<string>? previous) => Cached<string>.Load(previous, path, "Fixture", (location, _) =>
        {
            string file = location.Resolve();
            if (!AddonDependencies.FileExists(file)) return "absent";
            string value = ReadTrackedText(file);
            if (value == "broken") throw new InvalidDataException("fixture rejection");
            return value;
        }, default);
        var absent = Read(null); Assert.Same(absent, Read(absent));
        Directory.CreateDirectory(Path.GetDirectoryName(path.User)!); File.WriteAllText(path.User, "available");
        var present = Read(absent); Assert.Equal("available", present.Value);
        File.WriteAllText(path.User, "broken"); Assert.Throws<InvalidDataException>(() => Read(present));
        Assert.Equal("available", present.Value);
        File.WriteAllText(path.User, "restored"); Assert.Equal("restored", Read(present).Value);
    }

    [Fact]
    public void DecodedMeshBoundsRetainTheOriginalSurfaceOnlyConvention()
    {
        var model = new DecodedScienceModel([new(new Vector3d[] { new(-3, 2, 1), new(5, -4, 7) },
            ReadOnlyMemory<Vector3d>.Empty, ReadOnlyMemory<float>.Empty, new(new(1, 1, 1), 1, null))],
            new Dictionary<string, ReadOnlyMemory<byte>>(), 2)
            { Sprites = [new(new(1000, 1000, 1000), 1, default, 1)] };
        var bounds = model.MeshBounds();
        Assert.Equal(new Vector3d(1, -1, 4), bounds.Center);
        Assert.Equal(new Vector3d(8, 6, 6), bounds.Span);
    }

    [ScienceFact]
    [Trait("Category", "ExternalData")]
    public void RealJasonAndSorcePrepareWithoutAnEngineAndKeepTheirCacheIdentity()
    {
        string science = Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")!;
        var inputs = Inputs() with { Science = [new(science, "", "", "", true)] };
        var reader = new AddonPreparation(); var ephemeris = new SharedClockTests.StaticEphemeris();
        var prepared = reader.Prepare(ephemeris, null, inputs, null, default);
        Assert.Contains(prepared.World.Satellites, item => item.Name == "Jason-1");
        Assert.Equal(2, prepared.World.Satellites.Count);
        Assert.All(prepared.Science.SelectMany(source => source.Value), item => Assert.NotEmpty(item.Model.Surfaces));
        Assert.Same(prepared, reader.Prepare(ephemeris, prepared, inputs, null, default));
    }

    private sealed class ScienceFactAttribute : FactAttribute
    {
        public ScienceFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                Skip = "Set SPACETANGENT_SCIENCE_ROOT to the original Jason-1/SORCE add-on.";
        }
    }
}
