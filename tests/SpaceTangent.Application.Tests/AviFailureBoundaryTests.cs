// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class AviFailureBoundaryTests
{
    private static readonly byte[] Frame = [0xff, 0xd8, 1, 0xff, 0xd9];

    [Theory]
    [InlineData(1, 2, 30)] [InlineData(8193, 2, 30)] [InlineData(2, 1, 30)]
    [InlineData(2, 8193, 30)] [InlineData(2, 2, 0)] [InlineData(2, 2, 241)]
    public void UnsupportedDimensionsAndRatesAreRejectedBeforeWriting(int width, int height, int rate)
    {
        using var stream = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AviJpegWriter(stream, width, height, rate));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void NonemptyStreamsAndIncompleteJpegEnvelopesCannotBeRecorded()
    {
        using var nonempty = new MemoryStream(new byte[1]);
        Assert.Throws<ArgumentException>(() => new AviJpegWriter(nonempty, 2, 2, 1));
        using var stream = new MemoryStream(); using var writer = new AviJpegWriter(stream, 2, 2, 1);
        long length = stream.Length;
        Assert.Throws<ArgumentException>(() => writer.Append([0xff, 0xd8, 0]));
        Assert.Throws<ArgumentException>(() => writer.Append([0, 0, 0xff, 0xd9]));
        Assert.Equal(length, stream.Length); Assert.Equal(0, writer.FrameCount);
    }

    [Fact]
    public void ARecoverablePartialWriteRollsBackBeforeRetryAndFinalizesOneIndexedFrame()
    {
        using var stream = new FaultStream(); var writer = new AviJpegWriter(stream, 2, 2, 30);
        long start = stream.Length; stream.FailNext = true;
        Assert.Throws<IOException>(() => writer.Append(Frame));
        Assert.Equal(start, stream.Length); Assert.Equal(start, stream.Position); Assert.Equal(0, writer.FrameCount);
        Assert.True(writer.Append(Frame)); Assert.Equal(1, writer.FrameCount);
        writer.Dispose(); long end = stream.Length; writer.Dispose(); Assert.Equal(end, stream.Length);
        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        int index = bytes.AsSpan().IndexOf("idx1"u8);
        Assert.True(index > start); Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 4)));
        Assert.Equal(Frame.Length, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 20)));
        Assert.Throws<ObjectDisposedException>(() => writer.Append(Frame));
    }

    [Fact]
    public void AnUnrecoverablePartialWriteCannotProduceAValidLookingFooter()
    {
        using var stream = new FaultStream(); var writer = new AviJpegWriter(stream, 2, 2, 30);
        stream.FailNext = stream.BlockTruncate = true;
        Assert.Throws<IOException>(() => writer.Append(Frame));
        long brokenLength = stream.Length;
        Assert.Throws<InvalidOperationException>(() => writer.Append(Frame));
        Assert.Throws<InvalidDataException>(() => writer.Dispose());
        Assert.Equal(brokenLength, stream.Length); Assert.Equal(0, writer.FrameCount);
    }

    [Fact]
    public void TheRiffBudgetRefusesTheNextFrameWithoutAllocatingOrWritingIt()
    {
        using var stream = new MemoryStream(); using var writer = new AviJpegWriter(stream, 2, 2, 30);
        long headerEnd = stream.Position; stream.Position = 1L << 30;
        Assert.False(writer.Append(Frame)); Assert.Equal(headerEnd, stream.Length); Assert.Equal(0, writer.FrameCount);
        stream.Position = headerEnd;
    }

    private sealed class FaultStream : MemoryStream
    {
        internal bool FailNext, BlockTruncate;
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (!FailNext) { base.Write(bytes); return; }
            FailNext = false; base.Write(bytes[..1]); throw new IOException("Injected interrupted AVI write.");
        }
        public override void SetLength(long value)
        {
            if (BlockTruncate) throw new IOException("Injected unavailable rollback.");
            base.SetLength(value);
        }
    }
}
