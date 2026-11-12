// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class RecordingTests
{
    [Fact]
    public void AviFinalizesTheIndexAndFrameTiming()
    {
        using var output = new MemoryStream();
        using (var avi = new AviJpegWriter(output, 320, 240, 30))
        {
            Assert.True(avi.Append([0xff, 0xd8, 0x13, 0xff, 0xd9]));
            Assert.True(avi.Append([0xff, 0xd8, 0x10, 0x12, 0xff, 0xd9]));
            Assert.Throws<ArgumentException>(() => avi.Append([0xff, 0xd8]));
        }
        byte[] bytes = output.ToArray();
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.True(bytes.AsSpan(8, 4).SequenceEqual("AVI "u8));
        int main = bytes.AsSpan().IndexOf("avih"u8);
        Assert.Equal(33_333, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(main + 8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(main + 24)));
        int index = bytes.AsSpan().IndexOf("idx1"u8);
        Assert.Equal(32, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 20)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 36)));
    }

    [Fact]
    public void ObserverFramesRoundTripAndRejectCorruptedData()
    {
        var epoch = new JulianInstant(2451545, 12345, TimeScale.Tdb);
        var first = new ObserverView(epoch, CameraMode.Solar, null, false);
        var second = new ObserverView(epoch.AddSeconds(3600), CameraMode.Surface, null, false) { EarthObserver = EarthObserverCamera.Moscow };
        var scenario = new SimulationScenario([first, second], false, 0, 0, DeltaTModel.MorrisonStephenson2004);
        using var stream = new MemoryStream();
        using (var writer = new ObserverFrameSequence.Writer(stream, 30))
        {
            writer.Append(scenario);
            writer.Append(scenario with { ActiveView = 1 });
            writer.Complete();
        }
        byte[] original = stream.ToArray();
        stream.Position = 0;
        using var reader = new ObserverFrameSequence.Reader(stream);
        Assert.Equal(2, reader.FrameCount);
        Assert.Equal(scenario.Views, reader.ReadNext()!.Views);
        Assert.Equal(1, reader.ReadNext()!.ActiveView);
        Assert.Null(reader.ReadNext());
        original[^1] ^= 8;
        using var corrupt = new ObserverFrameSequence.Reader(new MemoryStream(original));
        Assert.NotNull(corrupt.ReadNext());
        Assert.Throws<InvalidDataException>(() => corrupt.ReadNext());
    }

    [ArchiveFact]
    [Trait("Category", "ExternalData")]
    public void TheArchivedFullMoonRecordingHasNineHundredCheckedFrames()
    {
        using var file = File.OpenRead(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_RECORDING")!);
        var report = LegacyFrameInspection.Inspect(file);
        Assert.Equal(10, report.RecordingSchemaMinor);
        Assert.Equal(900, report.Frames);
        Assert.Equal(141, report.Strings);
        Assert.Equal(2, report.StarFields);
        Assert.Equal(1, report.FirstSequence);
        Assert.Equal(900, report.LastSequence);
        Assert.Equal(2460600.5008, report.FirstTdb);
        Assert.Equal(report.FirstTdb, report.LastTdb);
    }

    private sealed class ArchiveFactAttribute : FactAttribute
    {
        public ArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_RECORDING")))
                Skip = "Set the original full-Moon frame recording.";
        }
    }
}
