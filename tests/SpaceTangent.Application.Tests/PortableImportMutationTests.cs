// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class PortableImportMutationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallModelTruncationsAndBinaryHeaderMutationsStayBounded(bool packed)
    {
        byte[] bytes = packed ? Glb() : Studio();
        string path = Path.GetTempFileName();
        try
        {
            DecodedScienceModel Decode() => packed ? PackedModelDecoder.Decode(path) : StudioMeshDecoder.Decode(path);
            File.WriteAllBytes(path, bytes);
            Assert.Equal(3, Assert.Single(Decode().Surfaces).Positions.Length);
            // A claimed complete length must not make a truncated document valid or allocate the claimed tail.
            for (int length = 0; length < bytes.Length; length++)
            {
                File.WriteAllBytes(path, bytes[..length]);
                Assert.Throws<InvalidDataException>(() => Decode());
            }
            var random = new Random(20261120);
            for (int iteration = 0; iteration < 128; iteration++)
            {
                byte[] changed = (byte[])bytes.Clone();
                int header = random.Next(packed ? 20 : 18);
                changed[header] ^= (byte)(1 << random.Next(8));
                File.WriteAllBytes(path, changed);
                long before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    var model = Decode();
                    Assert.True(double.IsFinite(model.Diameter));
                    Assert.InRange(model.Surfaces.Sum(surface => surface.Positions.Length), 3, 3);
                    Assert.All(model.Surfaces, surface => Assert.All(surface.Positions.ToArray(), position => Assert.True(position.IsFinite)));
                }
                catch (InvalidDataException) { }
                Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TruncatedAndMutatedScenariosNeverPartiallyRestoreASession()
    {
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var session = new SimulationSession(new(new FixedEphemeris(), []), epoch, DeltaTModel.MorrisonStephenson2004);
        session.RestoreScenario(session.CaptureScenario(0)); // Canonicalize the shared rate before testing rejection.
        var original = session.CaptureScenario(0);
        byte[] encoded = ScenarioFile.Encode(original);
        var random = new Random(20261120);
        var cases = Enumerable.Range(0, encoded.Length).Select(length => encoded[..length]).ToList();
        for (int index = 0; index < 128; index++)
        {
            byte[] changed = (byte[])encoded.Clone();
            changed[random.Next(changed.Length)] ^= (byte)(1 << random.Next(8));
            cases.Add(changed);
        }
        foreach (byte[] input in cases)
        {
            session.RestoreScenario(original);
            try { session.RestoreScenario(ScenarioFile.Decode(input)); }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                Assert.Equal(encoded, ScenarioFile.Encode(session.CaptureScenario(0)));
            }
        }
    }

    [Fact]
    public void InvalidUtf8PropertyNamesAreDataErrors()
    {
        byte[] json = [(byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'0', (byte)'}'];
        Assert.Contains("UTF-8", Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(json)).Message);
    }

    [Theory]
    [InlineData("dayTdb", -1L)]
    [InlineData("dayTdb", JulianInstant.MaximumDay + 1)]
    [InlineData("nanosecondsOfDay", -1L)]
    [InlineData("nanosecondsOfDay", JulianInstant.NanosecondsPerDay)]
    public void OutOfRangeEpochFieldsAreDataErrors(string field, long value)
    {
        var session = new SimulationSession(new(new FixedEphemeris(), []), JulianInstant.FromJulianDate(2451545, TimeScale.Tdb), DeltaTModel.FixedOffset);
        var json = JsonNode.Parse(ScenarioFile.Encode(session.CaptureScenario(0)))!;
        json["views"]![0]![field] = value;
        Assert.Throws<InvalidDataException>(() => ScenarioFile.Decode(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    private static byte[] Glb()
    {
        var surface = new DecodedSurface(new Vector3d[] { default, new(1, 0, 0), new(0, 1, 0) },
            Enumerable.Repeat(new Vector3d(0, 0, 1), 3).ToArray(), new float[] { 0, 0, 1, 0, 0, 1 }, new(new(1, 1, 1), 1, null));
        using var stream = new MemoryStream();
        PackedModelWriter.Write(new([surface], new Dictionary<string, ReadOnlyMemory<byte>>(), Math.Sqrt(2)), stream);
        return stream.ToArray();
    }

    private static byte[] Studio()
    {
        byte[] vertices = Payload(writer => { writer.Write((ushort)3); foreach (float x in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(x); });
        byte[] faces = Payload(writer => { writer.Write((ushort)1); foreach (ushort x in new ushort[] { 0, 1, 2, 0 }) writer.Write(x); });
        return Chunk(0x4d4d, Chunk(0x3d3d, Chunk(0x4000, [(byte)'t', 0, .. Chunk(0x4100, [.. Chunk(0x4110, vertices), .. Chunk(0x4120, faces)])])));
    }

    private static byte[] Chunk(ushort kind, byte[] payload) => Payload(writer => { writer.Write(kind); writer.Write(payload.Length + 6); writer.Write(payload); });
    private static byte[] Payload(Action<BinaryWriter> write)
    { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); write(writer); return stream.ToArray(); }
    private sealed class FixedEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}
