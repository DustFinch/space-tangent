// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SpaceTangent.Application;

public static class ObserverFrameSequence
{
    private const int HeaderLength = 20, DigestLength = 32, MaximumFrames = 180_000;
    private const ushort Version = 1;
    private static ReadOnlySpan<byte> Magic => "STFRAMES"u8;

    public sealed class Writer : IDisposable
    {
        private readonly Stream _stream;
        private bool _completed, _disposed;

        public Writer(Stream stream, int framesPerSecond)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanSeek || !stream.CanWrite || stream.Position != 0 || stream.Length != 0)
                throw new ArgumentException("Frame recording needs an empty writable, seekable stream.", nameof(stream));
            if (framesPerSecond is < 1 or > 240)
                throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
            _stream = stream;
            Span<byte> header = stackalloc byte[HeaderLength];
            header.Clear();
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], (ushort)framesPerSecond);
            _stream.Write(header);
        }

        public int FrameCount { get; private set; }

        public void Append(SimulationScenario scenario)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed || FrameCount >= MaximumFrames)
                throw new InvalidOperationException("Frame recording is complete or has reached its limit.");
            byte[] frame = ScenarioFile.Encode(scenario);
            Span<byte> header = stackalloc byte[4 + DigestLength];
            BinaryPrimitives.WriteInt32LittleEndian(header, frame.Length);
            _ = SHA256.HashData(frame, header[4..]);
            _stream.Write(header);
            _stream.Write(frame);
            FrameCount++;
        }

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
                return;
            long end = _stream.Position;
            _stream.Position = 12;
            Span<byte> footer = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(footer, FrameCount);
            "DONE"u8.CopyTo(footer[4..]);
            _stream.Write(footer);
            _stream.Position = end;
            _stream.Flush();
            _completed = true;
        }

        public void Dispose() => _disposed = true;
    }

    public sealed class Reader : IDisposable
    {
        private readonly Stream _stream;
        private bool _disposed;

        public Reader(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            if (!stream.CanRead)
                throw new ArgumentException("Frame recording input is unreadable.", nameof(stream));
            _stream = stream;
            Span<byte> header = stackalloc byte[HeaderLength];
            _stream.ReadExactly(header);
            if (!header[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != Version ||
                !header[16..].SequenceEqual("DONE"u8))
                throw new InvalidDataException("Invalid, unsupported or incomplete observer recording.");
            FramesPerSecond = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            FrameCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            if (FramesPerSecond is < 1 or > 240 || FrameCount is < 0 or > MaximumFrames)
                throw new InvalidDataException("Frame recording header is outside its limits.");
        }

        public int FramesPerSecond { get; }
        public int FrameCount { get; }
        public int FramesRead { get; private set; }

        public SimulationScenario? ReadNext()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (FramesRead == FrameCount)
            {
                if (_stream.ReadByte() != -1)
                    throw new InvalidDataException("Frame recording contains trailing data.");
                return null;
            }
            Span<byte> header = stackalloc byte[4 + DigestLength];
            _stream.ReadExactly(header);
            int size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size is <= 0 or > ScenarioFile.MaximumBytes)
                throw new InvalidDataException("Frame recording payload exceeds its size limit.");
            byte[] data = new byte[size];
            _stream.ReadExactly(data);
            Span<byte> digest = stackalloc byte[DigestLength];
            _ = SHA256.HashData(data, digest);
            if (!digest.SequenceEqual(header[4..]))
                throw new InvalidDataException("Observer frame digest does not match.");
            var scenario = ScenarioFile.Decode(data);
            FramesRead++;
            return scenario;
        }

        public void Dispose() => _disposed = true;
    }
}
