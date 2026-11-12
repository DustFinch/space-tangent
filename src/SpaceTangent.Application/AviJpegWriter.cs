// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;

namespace SpaceTangent.Application;

/// <summary>Writes one seekable RIFF AVI stream with independently decodable JPEG frames.</summary>
public sealed class AviJpegWriter : IDisposable
{
    private const long MaximumBytes = 1L << 30;
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;
    private readonly List<(int Offset, int Size)> _frames = [];
    private readonly long _riffSize, _totalFrames, _maximumBuffer, _streamLength, _streamBuffer, _listSize, _listStart;
    private int _largest;
    private bool _closed, _incomplete;

    public AviJpegWriter(Stream stream, int width, int height, int framesPerSecond)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite || !stream.CanSeek || stream.Position != 0 || stream.Length != 0)
            throw new ArgumentException("AVI needs an empty writable, seekable stream.", nameof(stream));
        if (width is < 2 or > 8192 || height is < 2 or > 8192 || framesPerSecond is < 1 or > 240)
            throw new ArgumentOutOfRangeException(nameof(width));
        _stream = stream;
        _writer = new(stream, Encoding.ASCII, leaveOpen: true);
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;

        Four("RIFF"u8); _riffSize = Placeholder(); Four("AVI "u8);
        Four("LIST"u8); _writer.Write(192); Four("hdrl"u8);
        Four("avih"u8); _writer.Write(56);
        _writer.Write(1_000_000 / framesPerSecond);
        _writer.Write(0); _writer.Write(0); _writer.Write(16);
        _totalFrames = Placeholder();
        _writer.Write(0); _writer.Write(1);
        _maximumBuffer = Placeholder();
        _writer.Write(width); _writer.Write(height);
        _writer.Write(0L); _writer.Write(0L);

        Four("LIST"u8); _writer.Write(116); Four("strl"u8);
        Four("strh"u8); _writer.Write(56); Four("vids"u8); Four("MJPG"u8);
        _writer.Write(0L); _writer.Write(0);
        _writer.Write(1); _writer.Write(framesPerSecond); _writer.Write(0);
        _streamLength = Placeholder();
        _streamBuffer = Placeholder();
        _writer.Write(-1); _writer.Write(0);
        _writer.Write((short)0); _writer.Write((short)0); _writer.Write((short)width); _writer.Write((short)height);
        Four("strf"u8); _writer.Write(40);
        _writer.Write(40); _writer.Write(width); _writer.Write(height); _writer.Write((short)1); _writer.Write((short)24);
        Four("MJPG"u8); _writer.Write(width * height * 3); _writer.Write(0L); _writer.Write(0L);

        Four("LIST"u8); _listSize = Placeholder(); _listStart = _stream.Position; Four("movi"u8);
    }

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public int FrameCount => _frames.Count;

    /// <returns>False when a complete next frame would exceed the RIFF size limit.</returns>
    public bool Append(ReadOnlySpan<byte> jpeg)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_incomplete)
            throw new InvalidOperationException("The AVI stream contains an incomplete write.");
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9)
            throw new ArgumentException("A frame must contain a complete JPEG image.", nameof(jpeg));
        long offset = _stream.Position;
        if (offset + 16L + jpeg.Length + (jpeg.Length & 1) + 16L * (_frames.Count + 1) > MaximumBytes)
            return false;
        try
        {
            Four("00dc"u8); _writer.Write(jpeg.Length); _writer.Write(jpeg);
            if ((jpeg.Length & 1) != 0)
                _writer.Write((byte)0);
        }
        catch (IOException)
        {
            _incomplete = true;
            try
            {
                _stream.SetLength(offset);
                _stream.Position = offset;
                _incomplete = false;
            }
            catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException) { }
            throw;
        }
        _frames.Add(((int)(offset - _listStart), jpeg.Length));
        _largest = Math.Max(_largest, jpeg.Length);
        return true;
    }

    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        try
        {
            if (_incomplete)
                throw new InvalidDataException("The AVI could not be finalized after an incomplete write.");
            long dataEnd = _stream.Position;
            Four("idx1"u8); _writer.Write(_frames.Count * 16);
            foreach (var (offset, size) in _frames)
            {
                Four("00dc"u8); _writer.Write(16); _writer.Write(offset); _writer.Write(size);
            }
            long end = _stream.Position;
            Patch(_riffSize, end - 8);
            Patch(_listSize, dataEnd - _listStart);
            Patch(_totalFrames, _frames.Count);
            Patch(_streamLength, _frames.Count);
            Patch(_maximumBuffer, _largest + 8);
            Patch(_streamBuffer, _largest + 8);
            _stream.Position = end;
        }
        finally { _writer.Dispose(); }
    }

    private long Placeholder()
    {
        long offset = _stream.Position;
        _writer.Write(0);
        return offset;
    }

    private void Patch(long offset, long value)
    {
        _stream.Position = offset;
        _writer.Write(checked((int)value));
    }

    private void Four(ReadOnlySpan<byte> code) => _writer.Write(code);
}
