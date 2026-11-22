// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SpaceTangent.Core.Time;
using Xunit;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyEnvelopeBoundaryTests
{
    [Theory]
    [InlineData("unknown-record")] [InlineData("large-string")] [InlineData("short-frame")]
    [InlineData("invalid-nanos")] [InlineData("missing-name")] [InlineData("missing-material")]
    [InlineData("native-flag")] [InlineData("native-basis")] [InlineData("short-body")]
    [InlineData("many-bodies")] [InlineData("bad-orientation")] [InlineData("outside-ephemeris")]
    public void InvalidIndependentWireRecordsCannotPublishAReplay(string fault)
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface");
        byte[] body = fixture.Body(); byte[] frame;
        switch (fault)
        {
            case "unknown-record": fixture.Record(255, []); break;
            case "large-string": fixture.Record(1, new byte[513]); break;
            default:
                switch (fault)
                {
                    case "missing-name": U(body, 0, 2); break;
                    case "missing-material": U(body, 48, 2); break;
                    case "native-flag": body[^1] = 2; break;
                    case "native-basis":
                        body[^1] = 1; Array.Resize(ref body, body.Length + 36);
                        F(body, body.Length - 36, float.NaN); break;
                    case "bad-orientation": F(body, 32, float.NaN); break;
                }
                frame = fixture.Frame(body);
                switch (fault)
                {
                    case "short-frame": frame = new byte[40]; break;
                    case "invalid-nanos": BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(32), -1); break;
                    case "many-bodies": H(frame, 98, 193); break;
                    case "short-body": Array.Resize(ref frame, fixture.BodyStart + 5); break;
                    case "outside-ephemeris": BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(24), 0); break;
                }
                fixture.Record(3, frame); break;
        }
        Assert.Throws<InvalidDataException>(() => new LegacyCameraSequence(new MemoryStream(fixture.File()),
            SharedClockTests.Create().World, DeltaTModel.MorrisonStephenson2004));
    }

    [Fact]
    public void ARecordedNativeBasisRetainsItsColumnsWithoutQuaternionSubstitution()
    {
        var fixture = new LegacyWireFixture().Names("Sol", "surface"); byte[] body = fixture.Body();
        body[^1] = 1; int start = body.Length; Array.Resize(ref body, start + 36);
        F(body, start + 4, 1); F(body, start + 12, -1); F(body, start + 32, 1);
        fixture.Record(3, fixture.Frame(body));
        using var replay = new LegacyCameraSequence(new MemoryStream(fixture.File()), SharedClockTests.Create().World,
            DeltaTModel.MorrisonStephenson2004);
        Assert.NotNull(replay.ReadNext()); var basis = Assert.Single(replay.CurrentGeometry!.Bodies).CameraBasis;
        Assert.Equal(new SpaceTangent.Contracts.Vector3d(0, 1, 0), basis.X);
        Assert.Equal(new SpaceTangent.Contracts.Vector3d(-1, 0, 0), basis.Y);
        Assert.Equal(new SpaceTangent.Contracts.Vector3d(0, 0, 1), basis.Z);
    }

    [Fact]
    public void LargeSkippedStellarEnvelopesStillVerifyTheirCompleteChecksum()
    {
        var fixture = new LegacyWireFixture().Record(5, new byte[100_000]); byte[] bytes = fixture.File();
        var summary = LegacyFrameInspection.Inspect(new MemoryStream(bytes)); Assert.Equal(1, summary.StarFields);
        bytes[24 + 5 + 90_000] ^= 1;
        Assert.Throws<InvalidDataException>(() => LegacyFrameInspection.Inspect(new MemoryStream(bytes)));
    }
}
