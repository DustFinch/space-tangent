// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class StudioSmoothingTests
{
    [Theory]
    [InlineData(1u, 1u, true)]
    [InlineData(3u, 2u, true)]
    [InlineData(1u, 2u, false)]
    [InlineData(0u, 1u, false)]
    public void GroupOverlapSmoothsSharedVerticesWithAreaWeights(uint first, uint second, bool smooth)
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] vertices = Payload(writer =>
            {
                writer.Write((ushort)4);
                foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2 })
                    writer.Write(value);
            });
            byte[] groups = Chunk(0x4150, Payload(writer => { writer.Write(first); writer.Write(second); }));
            byte[] faces = Payload(writer =>
            {
                writer.Write((ushort)2);
                foreach (ushort value in new ushort[] { 0, 1, 2, 0, 0, 3, 1, 0 })
                    writer.Write(value);
                writer.Write(groups);
            });
            byte[] mesh = Chunk(0x4100, [.. Chunk(0x4110, vertices), .. Chunk(0x4120, faces)]);
            File.WriteAllBytes(path, Chunk(0x4d4d, Chunk(0x3d3d, Chunk(0x4000, [(byte)'w', 0, .. mesh]))));
            var normals = Assert.Single(StudioMeshDecoder.Decode(path).Surfaces).Normals.ToArray();
            Vector3d expected = smooth ? new Vector3d(0, 2, 1) / Math.Sqrt(5) : new(0, 0, 1);
            Assert.InRange((normals[0] - expected).Length, 0, 1e-12);
            Assert.InRange((normals[2] - new Vector3d(0, 0, 1)).Length, 0, 1e-12); // Unshared vertex stays flat.
            if (smooth)
                Assert.InRange((normals[3] - expected).Length, 0, 1e-12);
        }
        finally { File.Delete(path); }
    }

    private static byte[] Chunk(ushort kind, byte[] payload) => Payload(writer =>
    {
        writer.Write(kind);
        writer.Write(payload.Length + 6);
        writer.Write(payload);
    });

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
