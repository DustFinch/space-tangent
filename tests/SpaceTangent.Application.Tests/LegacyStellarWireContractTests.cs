// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyStellarWireContractTests
{
    private static SimulationWorld World => new(new SharedClockTests.StaticEphemeris(), []);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(7)]
    public void IndependentArrayBlocksRetainBackgroundAndMovingStarData(byte flags)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(5, Field(0, flags)); fixture.Record(5, Field(1, 3));
        fixture.Record(3, fixture.Frame(overlays: View().Bytes));
        var summary = LegacyFrameInspection.Inspect(new MemoryStream(fixture.File()));
        Assert.Equal(2, summary.StarFields);
        using var replay = Read(fixture); Assert.NotNull(replay.ReadNext());
        var view = Assert.IsType<RecordedStellarView>(replay.CurrentGeometry!.StellarView);
        Assert.Equal(1, view.Background.Count);
        Assert.Equal(flags, view.Background.Flags);
        Assert.Equal(new float[] { 1, 0, 0 }, view.Background.Directions.ToArray());
        Assert.Equal(new float[] { 2 }, view.Background.Magnitudes.ToArray());
        Assert.Equal(new byte[] { 255, 128, 64 }, view.Background.Colors.ToArray());
        Assert.Equal((flags & 1) == 0 ? [] : new float[] { 10 }, view.Background.Distances.ToArray());
        Assert.Equal((flags & 2) == 0 ? [] : new float[] { 0, .25f, 0 }, view.Background.Velocities.ToArray());
        Assert.Equal(1, view.Periodic!.Count);
        Assert.Equal(12, view.PeriodicPositions.Length);
        Assert.Equal(1, BinaryPrimitives.ReadSingleLittleEndian(view.PeriodicPositions.Span));
        Assert.Equal(Basis3d.Identity, view.ToCamera);
        var ground = Assert.IsType<RecordedGroundStars>(view.Ground);
        Assert.Equal(new Vector3d(0, 1, 0), ground.Zenith);
        Assert.Equal(.5f, ground.Refraction);
        Assert.Equal(.25f, ground.Twinkle);
        Assert.Equal(.125f, ground.Glow);
        Assert.Equal(3f, ground.Time);
    }

    [Theory]
    [InlineData(6, 2f)]
    [InlineData(18, float.NaN)]
    [InlineData(25, 0f)]
    [InlineData(25, -1f)]
    [InlineData(29, float.PositiveInfinity)]
    public void InvalidStarArraysAreRejected(int offset, float value)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        byte[] field = Field(0, 3); F(field, offset, value);
        fixture.Record(5, field); fixture.Record(3, fixture.Frame(overlays: View(periodic: false).Bytes));
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Theory]
    [InlineData(0, 2)] [InlineData(5, 8)]
    public void InvalidFieldSlotOrFlagsAreRejected(int offset, byte value)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        byte[] field = Field(0, 3); field[offset] = value;
        fixture.Record(5, field); fixture.Record(3, fixture.Frame(overlays: View(periodic: false).Bytes));
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Fact]
    public void PeriodicStarsRequireDistancesAndMatchingPositionCount()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(5, Field(0, 3)); fixture.Record(5, Field(1, 0));
        fixture.Record(3, fixture.Frame(overlays: View().Bytes));
        Assert.Throws<InvalidDataException>(() => Read(fixture));
        var mismatch = new LegacyWireFixture().Names("Sol", "surface");
        mismatch.Record(5, Field(0, 3)); mismatch.Record(5, Field(1, 3));
        mismatch.Record(3, mismatch.Frame(overlays: View(periodic: false).Bytes));
        Assert.Throws<InvalidDataException>(() => Read(mismatch));
    }

    [Theory]
    [InlineData("near-cut", -1f)]
    [InlineData("brightness", -1f)]
    [InlineData("faintest", float.NaN)]
    [InlineData("zenith", 2f)]
    [InlineData("refraction", -1f)]
    [InlineData("twinkle", -1f)]
    [InlineData("glow", -1f)]
    [InlineData("beta", 1f)]
    [InlineData("position", float.PositiveInfinity)]
    public void InvalidStellarObserverValuesAreRejected(string name, float value)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(5, Field(0, 3)); fixture.Record(5, Field(1, 3));
        var view = View(); F(view.Bytes, view.Positions[name], value);
        fixture.Record(3, fixture.Frame(overlays: view.Bytes));
        Assert.Throws<InvalidDataException>(() => Read(fixture));
    }

    [Fact]
    public void EmptyFieldRecordsAndDisabledViewsClearPreviousStellarGeometry()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(5, Field(0, 3)); fixture.Record(5, Field(1, 3));
        fixture.Record(3, fixture.Frame(overlays: View().Bytes));
        fixture.Record(5, new byte[5]); fixture.Record(5, new byte[] { 1, 0, 0, 0, 0, 0 });
        fixture.Record(3, fixture.Frame(sequence: 2));
        using var replay = Read(fixture); replay.ReadNext();
        Assert.NotNull(replay.CurrentGeometry!.StellarView);
        replay.ReadNext();
        Assert.Null(replay.CurrentGeometry!.StellarView);
    }

    [Fact]
    public void IdenticalStellarPositionsReuseTheirPayloadMemoryAcrossFrames()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        fixture.Record(5, Field(0, 3)); fixture.Record(5, Field(1, 3));
        byte[] frame = fixture.Frame(overlays: View().Bytes);
        fixture.Record(3, frame); fixture.Record(3, frame);
        using var replay = Read(fixture); replay.ReadNext();
        var first = replay.CurrentGeometry!.StellarView!;
        replay.ReadNext(); var second = replay.CurrentGeometry!.StellarView!;
        Assert.Same(first.Background, second.Background);
        Assert.Equal(first.PeriodicPositions, second.PeriodicPositions);
    }

    private static LegacyCameraSequence Read(LegacyWireFixture fixture) =>
        new(new MemoryStream(fixture.File()), World, DeltaTModel.MorrisonStephenson2004);

    private static byte[] Field(byte slot, byte flags)
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write(slot); writer.Write(1); writer.Write(flags);
            Vector(writer, 1, 0, 0); writer.Write(2f); writer.Write(new byte[] { 255, 128, 64 });
            if ((flags & 1) != 0) writer.Write(10f);
            if ((flags & 2) != 0) Vector(writer, 0, .25f, 0);
            if ((flags & 4) != 0) writer.Write(42u);
        }
        return output.ToArray();
    }

    private static (byte[] Bytes, Dictionary<string, int> Positions) View(bool periodic = true)
    {
        using var output = new MemoryStream();
        var positions = new Dictionary<string, int>();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)0); writer.Write((byte)1); // absent lens, present stellar view
            Vector(writer, 0, 0, 0); Vector(writer, 0, 0, 0);
            Mark("near-cut"); writer.Write(0f); Mark("faintest"); writer.Write(20f);
            Mark("brightness"); writer.Write(1f); writer.Write(1f);
            writer.Write((byte)1); Mark("zenith"); Vector(writer, 0, 1, 0);
            Mark("refraction"); writer.Write(.5f); Mark("twinkle"); writer.Write(.25f); Mark("glow"); writer.Write(.125f);
            Mark("beta"); Vector(writer, .001f, 0, 0); writer.Write(3f);
            if (periodic) { Mark("position"); Vector(writer, 1, 2, 3); }
        }
        return (output.ToArray(), positions);
        void Mark(string name) => positions.Add(name, checked((int)output.Position));
    }

    private static void Vector(BinaryWriter writer, float x, float y, float z)
    { writer.Write(x); writer.Write(y); writer.Write(z); }
}
