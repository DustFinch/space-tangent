// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SpaceTangent.Core.Ephemerides;
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

    [ArchiveFact]
    [Trait("Category", "ExternalData")]
    public void RebuildsTheArchivedFullMoonCameraFromTheRecordedProjection()
    {
        string archive = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_RECORDING")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        const DeltaTModel deltaT = DeltaTModel.MorrisonStephenson2004;
        string report = Path.Combine(Path.GetDirectoryName(archive)!, "record.json");
        using var document = JsonDocument.Parse(File.ReadAllText(report));
        string observer = document.RootElement.GetProperty("scientific").GetProperty("observerState").GetString()!;
        using var original = new MemoryStream(Encoding.UTF8.GetBytes(observer));
        var expected = SceneLink.ReadArchivedObserver(original, world, deltaT).Views.Single();

        byte[] data = File.ReadAllBytes(archive);
        using var stream = new MemoryStream(data);
        using var replay = new LegacyCameraSequence(stream, world, deltaT);
        Assert.Equal(900, replay.FrameCount);
        Assert.Equal(30, replay.FramesPerSecond);
        var first = replay.ReadNext()!;
        var geometry = Assert.IsType<RecordedSceneGeometry>(replay.CurrentGeometry);
        Assert.Equal(65, geometry.Bodies.Count);
        Assert.Equal(6378.1366, geometry.KilometersPerUnit);
        var savedMoon = Assert.Single(geometry.Bodies, body => body.Name == "Sol/Earth/Moon");
        Assert.Equal(SolarSystemSample.MoonId, savedMoon.Id);
        Assert.InRange(savedMoon.Radius, 1.08990, 1.08992);
        Assert.InRange(savedMoon.CameraPosition.Z, -55.52255, -55.52253);
        Assert.InRange(savedMoon.CameraBasis.X.Length, .99999, 1.00001);
        var view = Assert.Single(first.Views);
        Assert.Equal(deltaT, first.DeltaT);
        Assert.Equal(SolarSystemSample.EarthId, view.Selection);
        Assert.Equal(expected.Epoch, view.Epoch);
        Assert.Equal(10, view.FieldOfViewDegrees);
        var expectedCamera = expected.Navigation!.Value;
        var actualCamera = view.Navigation!.Value;
        var actualPose = actualCamera.Placement!.Value;
        var expectedPose = expectedCamera.Placement!.Value;
        var displacement = actualCamera.AnchorKilometers - expectedCamera.AnchorKilometers +
            actualPose.OffsetKilometers - expectedPose.OffsetKilometers;
        Assert.InRange(displacement.Length, 0, 17);
        Vector3d relative = actualPose.OffsetKilometers * -1;
        var right = Vector3d.Cross(actualPose.Forward, actualPose.Up);
        Vector3d cameraSpace = new(Vector3d.Dot(relative, right), Vector3d.Dot(relative, actualPose.Up),
            -Vector3d.Dot(relative, actualPose.Forward));
        // The archive measures the nearby Earth in units of its equatorial radius.
        Vector3d recordedEarth = new Vector3d(.002617646474391222, -.8801857233047485, .4699283242225647) * 6378.1366;
        Assert.InRange((cameraSpace - recordedEarth).Length, 0, .000001);
        Assert.InRange((actualPose.Forward - expectedPose.Forward).Length, 0, 1e-6);
        Assert.InRange((actualPose.Up - expectedPose.Up).Length, 0, 1e-6);
        for (int index = 1; index < replay.FrameCount; index++)
            Assert.Equal(view, Assert.Single(replay.ReadNext()!.Views));
        Assert.Null(replay.ReadNext());

        data[^1] ^= 1;
        using var corrupt = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => new LegacyCameraSequence(corrupt, world, deltaT));
    }

    [ArchiveFact(true)]
    public void ReadsSavedOrbitsAndLabelsFromTheOriginalWindowsRecorder()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_ORBITS")!;
        var world = new SimulationWorld(De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []);
        using var file = File.OpenRead(path);
        using var replay = new LegacyCameraSequence(file, world, DeltaTModel.MorrisonStephenson2004);
        Assert.NotNull(replay.ReadNext());
        var frame = Assert.IsType<RecordedSceneGeometry>(replay.CurrentGeometry);
        Assert.Equal(66, frame.Orbits.Count);
        Assert.Equal(115, frame.Labels.Count);
        var planet = Assert.Single(frame.Bodies, body => body.Name == "Sol/Earth");
        Assert.InRange((planet.AtmosphereRadius - planet.Radius) * frame.KilometersPerUnit, 71.8, 72.0);
        Assert.Contains(frame.Labels, label => label.Text == "Альтаир");
        var earth = Assert.Single(frame.Orbits, orbit => orbit.Name == "Sol/Earth");
        Assert.True(earth.Points.Count > 32);
        Assert.True(earth.CameraPoint(0).IsFinite);
        Assert.NotEqual(earth.CameraPoint(0), earth.CameraPoint(1));
        Assert.NotNull(replay.ReadNext());
        var next = Assert.Single(replay.CurrentGeometry!.Orbits, orbit => orbit.Name == earth.Name);
        Assert.Same(earth.Points, next.Points);
    }

    private sealed class ArchiveFactAttribute : FactAttribute
    {
        public ArchiveFactAttribute(bool orbits = false)
        {
            string input = orbits ? "SPACETANGENT_LEGACY_ORBITS" : "SPACETANGENT_LEGACY_RECORDING";
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(input)))
                Skip = "Set the original full-Moon frame recording.";
        }
    }
}
