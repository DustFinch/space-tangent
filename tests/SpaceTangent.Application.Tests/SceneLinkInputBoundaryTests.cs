// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SceneLinkInputBoundaryTests
{
    private const string Valid = "cel://Follow/Sol:Earth/2024-04-09T00:00:00.00000?x=AAAAAAAAAAAB&y=&z=&ow=1&ox=0&oy=0&oz=0&select=Sol:Earth:Moon&fov=45&ts=1&p=1&ver=3";

    [Theory]
    [InlineData("scheme")] [InlineData("parameters")] [InlineData("frame")]
    [InlineData("path")] [InlineData("duplicate")] [InlineData("empty-key")]
    [InlineData("version")] [InlineData("date")] [InlineData("quaternion")]
    [InlineData("field")] [InlineData("pause")] [InlineData("rate")]
    [InlineData("long-coordinate")] [InlineData("wide-coordinate")] [InlineData("long-link")]
    public void MalformedLinksCannotChangeASession(string fault)
    {
        var session = SharedClockTests.Create(); byte[] before = ScenarioFile.Encode(session.CaptureScenario(0));
        string link = fault switch
        {
            "scheme" => "unknown://scene",
            "parameters" => "cel://Follow/Sol:Earth/2024-04-09",
            "frame" => Valid.Replace("Follow/", "Unknown/", StringComparison.Ordinal),
            "path" => Valid.Replace("Follow/Sol:Earth/", "Follow/", StringComparison.Ordinal),
            "duplicate" => Valid + "&ver=3",
            "empty-key" => Valid + "&=value",
            "version" => Valid.Replace("ver=3", "ver=2", StringComparison.Ordinal),
            "date" => Valid.Replace("2024-04-09T00:00:00.00000", "invalid-date", StringComparison.Ordinal),
            "quaternion" => Valid.Replace("ow=1", "ow=0", StringComparison.Ordinal),
            "field" => Valid.Replace("fov=45", "fov=0", StringComparison.Ordinal),
            "pause" => Valid.Replace("p=1", "p=2", StringComparison.Ordinal),
            "rate" => Valid.Replace("ts=1", "ts=1e300", StringComparison.Ordinal),
            "long-coordinate" => Valid.Replace("x=AAAAAAAAAAAB", "x=" + new string('A', 25), StringComparison.Ordinal),
            "wide-coordinate" => Valid.Replace("x=AAAAAAAAAAAB", "x=" + Convert.ToBase64String(new byte[17]), StringComparison.Ordinal),
            _ => new string('x', SceneLink.MaximumLength + 1)
        };
        Assert.Throws<FormatException>(() => SceneLink.Read(link, session.World, session.DeltaT));
        Assert.Equal(before, ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void AnUnavailableEpochOrEmptyReferenceCannotProduceAnObserver()
    {
        var session = SharedClockTests.Create();
        Assert.Throws<InvalidDataException>(() => SceneLink.Read(Valid.Replace("2024-04-09", "9999-04-09", StringComparison.Ordinal),
            session.World, DeltaTModel.MorrisonStephenson2004));
        Assert.Throws<InvalidDataException>(() => SceneLink.ResolveObject(":://", session.World.Catalogue));
    }
}
