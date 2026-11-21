// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyOverlayContractTests
{
    private static SimulationWorld World => new(new SharedClockTests.StaticEphemeris(), []);

    [Fact]
    public void IndependentOverlayRecordsRetainGeometryColorsAndUnicode()
    {
        var (fixture, frame, _) = RichFrame();
        fixture.Record(3, frame);
        using var replay = Read(fixture);
        Assert.NotNull(replay.ReadNext());
        var geometry = replay.CurrentGeometry!;
        var star = Assert.Single(geometry.StarPoints);
        Assert.Equal(("marker", new Vector3d(1, 0, 0), .75), (star.Name, star.CameraDirection, star.Brightness));
        var orbit = Assert.Single(geometry.Orbits);
        Assert.Equal(new Vector3d(-.75, 0, 0), orbit.Points[0]);
        Assert.Equal(new Vector3d(1.5, 0, 0), orbit.Points[1]);
        Assert.Equal(new Vector3d(19, 41, 61.5), orbit.CameraPoint(0));
        Assert.Equal(.5, orbit.Fade);
        var shadow = Assert.Single(geometry.Shadows);
        Assert.Equal("Sol", shadow.Receiver);
        Assert.Equal(.5, shadow.Radius);
        var point = Assert.Single(geometry.BodyPoints).Value;
        Assert.Equal("Sol", point.Name);
        Assert.True(point.ReplacesSurface);
        var label = Assert.Single(geometry.Labels);
        Assert.Equal("Луна — обзор", label.Text);
        Assert.Equal(3, label.Kind);
        Assert.Equal(.75, label.Opacity);
        Assert.Equal(new Vector3d(1, .5, .25), label.Color);
        var tail = Assert.Single(geometry.CometTails);
        Assert.Equal("tail", tail.Name);
        Assert.Equal((10d, 2d, .5), (tail.Length, tail.Width, tail.Brightness));
    }

    [Theory]
    [InlineData("star-direction", 2f)]
    [InlineData("star-brightness", 2f)]
    [InlineData("orbit-scale", 0f)]
    [InlineData("shadow-radius", 0f)]
    [InlineData("label-opacity", -1f)]
    [InlineData("label-marker", -1f)]
    [InlineData("tail-direction", 0f)]
    [InlineData("tail-length", 0f)]
    [InlineData("tail-width", -1f)]
    [InlineData("tail-coma", -1f)]
    [InlineData("point-color", float.NaN)]
    public void InvalidTypedOverlayValuesAreRejected(string field, float value)
    {
        var (fixture, frame, positions) = RichFrame();
        F(frame, positions[field], value);
        fixture.Record(3, frame);
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Theory]
    [InlineData("star-name")]
    [InlineData("orbit-geometry")]
    [InlineData("label-text")]
    public void UndefinedStringAndGeometryReferencesAreRejected(string field)
    {
        var (fixture, frame, positions) = RichFrame();
        U(frame, positions[field], 99);
        fixture.Record(3, frame);
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Theory]
    [InlineData("orbit-frame", 2)]
    [InlineData("orbit-color", 2)]
    [InlineData("point-visible", 2)]
    [InlineData("label-kind", 10)]
    public void UnsupportedOverlayFlagsAreRejected(string field, byte value)
    {
        var (fixture, frame, positions) = RichFrame();
        frame[positions[field]] = value;
        fixture.Record(3, frame);
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(7)] [InlineData(11)]
    public void SupportedLensVersionsRetainDiskConventionsAndSpin(int minor)
    {
        var fixture = new LegacyWireFixture(minor).Names("Sol", "surface", "bh/example");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1); writer.Write(2u);
            Vector(writer, 0, 0, -20); writer.Write(1f);
            if (minor >= 2) { Vector(writer, 0, 0, 1); writer.Write(8f); writer.Write(3000f); }
            if (minor >= 3) { Vector(writer, 1, 0, 0); writer.Write((byte)1); }
            if (minor >= 7) writer.Write(.5f);
            if (minor >= 10) writer.Write((byte)0);
        }
        fixture.Record(3, fixture.Frame(overlays: bytes.ToArray()));
        using var replay = Read(fixture); Assert.NotNull(replay.ReadNext());
        var lens = Assert.IsType<RecordedLens>(replay.CurrentGeometry!.Lens);
        Assert.Equal("bh/example", lens.Name);
        Assert.Equal(1, lens.Radius);
        Assert.Equal(minor >= 7 ? .5 : 0, lens.Spin);
        Assert.Equal(minor >= 3, lens.Cinematic);
        Assert.Equal(minor == 1 ? default : new Vector3d(0, 0, 1), lens.DiskNormal);
        Assert.Equal(minor == 1 ? default : minor == 2 ? new(0, 1, 0) : new Vector3d(1, 0, 0), lens.DiskReference);
    }

    [Fact]
    public void UnchangedOverlayCollectionsAreReusedBetweenFrames()
    {
        var (fixture, frame, _) = RichFrame();
        fixture.Record(3, frame); fixture.Record(3, frame);
        using var replay = Read(fixture); replay.ReadNext(); var first = replay.CurrentGeometry!;
        replay.ReadNext(); var second = replay.CurrentGeometry!;
        Assert.Same(first.StarPoints, second.StarPoints);
        Assert.Same(first.BodyPoints, second.BodyPoints);
        Assert.Same(first.Shadows, second.Shadows);
        Assert.Same(first.CometTails, second.CometTails);
        Assert.Equal(first.Orbits, second.Orbits);
        Assert.Equal(first.Labels, second.Labels);
    }

    private static LegacyCameraSequence Read(LegacyWireFixture fixture) =>
        new(new MemoryStream(fixture.File()), World, DeltaTModel.MorrisonStephenson2004);

    private static (LegacyWireFixture Fixture, byte[] Frame, Dictionary<string, int> Positions) RichFrame()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface", "Луна — обзор", "marker", "tail");
        using var shape = new MemoryStream();
        using (var writer = new BinaryWriter(shape, System.Text.Encoding.UTF8, true))
        {
            writer.Write(2u); Vector(writer, -1, 0, 0); Vector(writer, 1, 0, 0);
            Vector(writer, .25f, 0, 0); Vector(writer, .5f, 0, 0);
        }
        fixture.Record(2, shape.ToArray());
        using var bytes = new MemoryStream();
        var positions = new Dictionary<string, int>();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            Mark("star-name"); writer.Write(3u); Mark("star-direction"); Vector(writer, 1, 0, 0); Mark("star-brightness"); writer.Write(.75f);
            writer.Write(0u); Mark("orbit-geometry"); writer.Write(0u);
            Vector(writer, 10, 20, 30); Vector(writer, .25f, .5f, .75f); Mark("orbit-scale"); writer.Write(2f);
            Mark("orbit-frame"); writer.Write((byte)0); Mark("orbit-color"); writer.Write((byte)1); Vector(writer, .25f, .5f, 1); writer.Write(.5f);
            writer.Write(0u); Vector(writer, 0, 0, -5); Mark("shadow-radius"); writer.Write(.5f);
            writer.Write(0u); writer.Write(1f); Mark("point-color"); Vector(writer, 1, .75f, .5f); writer.Write(.5f);
            Mark("point-visible"); writer.Write((byte)1);
            writer.Write(0u); Mark("label-text"); writer.Write(2u); Vector(writer, 1, 2, -10);
            Mark("label-kind"); writer.Write((byte)3); Mark("label-opacity"); writer.Write(.75f);
            writer.Write((byte)1); Vector(writer, 1, .5f, .25f); Mark("label-marker"); writer.Write(.125f);
            writer.Write(4u); Vector(writer, 0, 0, -4); Mark("tail-direction"); Vector(writer, 1, 0, 0); Vector(writer, 0, 1, 0);
            Mark("tail-length"); writer.Write(10f); Mark("tail-width"); writer.Write(2f); writer.Write(.5f); Vector(writer, 1, 1, 1);
            Mark("tail-coma"); writer.Write(.25f);
            writer.Write((byte)0); writer.Write((byte)0); // no compact lens or continuous stellar field
        }
        byte[] frame = fixture.Frame(overlays: bytes.ToArray());
        foreach (int offset in new[] { 100, 102, 104, 106, 108, 110 }) H(frame, offset, 1);
        int start = fixture.BodyStart + fixture.Body().Length;
        return (fixture, frame, positions.ToDictionary(pair => pair.Key, pair => pair.Value + start));
        void Mark(string name) => positions.Add(name, checked((int)bytes.Position));
    }

    private static void Vector(BinaryWriter writer, float x, float y, float z)
    { writer.Write(x); writer.Write(y); writer.Write(z); }
}
