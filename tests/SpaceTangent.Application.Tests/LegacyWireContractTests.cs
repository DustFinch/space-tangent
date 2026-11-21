// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyWireContractTests
{
    private static SimulationWorld World => new(new SharedClockTests.StaticEphemeris(), []);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void SupportedMinorVersionsDecodeAnIndependentSingleBodyFrame(int minor)
    {
        var fixture = new LegacyWireFixture(minor).Names("Sol", "surface");
        byte[] frame = fixture.Frame();
        fixture.Record(3, frame);
        byte[] bytes = fixture.File();
        var report = LegacyFrameInspection.Inspect(new MemoryStream(bytes));
        Assert.Equal((minor, 2, 1, 2), (report.RecordingSchemaMinor, report.FrameSchema, report.Frames, report.Strings));
        using var replay = new LegacyCameraSequence(new MemoryStream(bytes), World, DeltaTModel.MorrisonStephenson2004);
        var restored = replay.ReadNext()!;
        var view = Assert.Single(restored.Views);
        Assert.Equal(new JulianInstant(2451545, 0, TimeScale.Tdb), view.Epoch);
        Assert.Equal(45, view.FieldOfViewDegrees);
        Assert.Equal(SolarSystemSample.SunId, view.Selection);
        Assert.Equal(100, view.Navigation!.Value.RangeKilometers);
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, restored.DeltaT);
        Assert.Null(replay.ReadNext());
        var geometry = Assert.IsType<RecordedSceneGeometry>(replay.CurrentGeometry);
        var body = Assert.Single(geometry.Bodies);
        Assert.Equal("Sol", body.Name);
        Assert.Equal("surface", body.MaterialId);
        Assert.Equal(new Vector3d(0, 0, -100), body.CameraPosition);
        Assert.Equal(Basis3d.Identity, body.CameraBasis);
        Assert.Equal(1, body.Radius);
        Assert.Equal(2, body.AtmosphereRadius);
        Assert.Equal(minor >= 5 ? new(1, .5, .25) : new Vector3d(1, 1, 1), body.LightColor);
        Assert.Equal(30, replay.FramesPerSecond);
        replay.Dispose();
        Assert.Throws<ObjectDisposedException>(() => replay.ReadNext());
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(10)] [InlineData(12)] [InlineData(14)] [InlineData(16)] [InlineData(20)]
    public void InvalidFileHeadersFailBeforeReturningASummary(int offset)
    {
        var fixture = new LegacyWireFixture();
        byte[] bytes = fixture.File(header => header[offset] ^= 0x40);
        Assert.Throws<InvalidDataException>(() => LegacyFrameInspection.Inspect(new MemoryStream(bytes)));
    }

    [Fact]
    public void FootersChecksumsAndTrailingBytesAreIndependentlyValidated()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(3, fixture.Frame());
        byte[] badCount = fixture.File(changeFooter: footer => U(footer, 0, 2));
        Assert.Throws<InvalidDataException>(() => LegacyFrameInspection.Inspect(new MemoryStream(badCount)));
        byte[] badCrc = fixture.File(); badCrc[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => LegacyFrameInspection.Inspect(new MemoryStream(badCrc)));
        Assert.Throws<InvalidDataException>(() => LegacyFrameInspection.Inspect(new MemoryStream(fixture.File().Concat(new byte[] { 0 }).ToArray())));
    }

    [Fact]
    public void CancellationAndTruncationNeverReturnAPartialSummary()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(3, fixture.Frame());
        Assert.Throws<OperationCanceledException>(() => LegacyFrameInspection.Inspect(new MemoryStream(fixture.File()), new CancellationToken(true)));
        Assert.Throws<EndOfStreamException>(() => LegacyFrameInspection.Inspect(new MemoryStream(fixture.File()[..^1])));
    }

    [Theory]
    [InlineData(16, 0)] [InlineData(16, -1)] [InlineData(65, 0)]
    [InlineData(60, .5f)] [InlineData(52, -1)] [InlineData(56, -1)]
    [InlineData(93, -1)] [InlineData(117, 1001)] [InlineData(129, -1)]
    [InlineData(36, float.NaN)] [InlineData(105, float.PositiveInfinity)]
    public void InvalidBodyGeometryIsRejectedBeforePublishingAReplay(int offset, float value)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        byte[] body = fixture.Body(); F(body, offset, value);
        fixture.Record(3, fixture.Frame(body));
        Assert.Throws<InvalidDataException>(() => new LegacyCameraSequence(new MemoryStream(fixture.File()), World, DeltaTModel.EspenakMeeus2006));
    }

    [Theory]
    [InlineData(45, 0)] [InlineData(45, float.NaN)] [InlineData(77, 0)] [InlineData(93, -1)]
    public void InvalidCameraProjectionAndSolarGeometryAreRejected(int offset, float value)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        byte[] frame = fixture.Frame(); F(frame, offset, value);
        fixture.Record(3, frame);
        Assert.Throws<InvalidDataException>(() => new LegacyCameraSequence(new MemoryStream(fixture.File()), World, DeltaTModel.EspenakMeeus2006));
    }

    [Fact]
    public void RepeatedFramesRetainTheirIndependentEpochAndShareUnchangedGeometry()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(3, fixture.Frame());
        byte[] second = fixture.Frame(sequence: 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(second.AsSpan(32), 3600L * 1_000_000_000);
        fixture.Record(3, second);
        using var replay = new LegacyCameraSequence(new MemoryStream(fixture.File()), World, DeltaTModel.MorrisonStephenson2004);
        var first = replay.ReadNext()!; var geometry = replay.CurrentGeometry;
        var next = replay.ReadNext()!;
        Assert.Equal(first.Views[0].Epoch.AddSeconds(3600), next.Views[0].Epoch);
        Assert.Equal(first.DeltaT, next.DeltaT);
        Assert.Same(geometry, replay.CurrentGeometry);
        Assert.Null(replay.ReadNext());
    }
}
