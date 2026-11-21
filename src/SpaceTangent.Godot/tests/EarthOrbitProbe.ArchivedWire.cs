// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Tests;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedWire(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        // Old scenes may inherit their rate from the camera mode. Persist an
        // explicit effective rate so equality also checks the restored clock.
        for (int index = 0; index < session.ViewCount; index++)
            session.SetPlaybackRate(index, session.CurrentPlaybackRate(index));
        var original = session.CaptureScenario(0);
        string previous = Path.Combine(directory, "before-independent-wire.stscene");
        if (!scene.SaveScenario(previous)) throw new IOException("Could not preserve the live workspace.");
        var fixture = new LegacyWireFixture().Names("Sol/Earth", "world/Sol/Earth", "Sol/Earth/Moon",
            "world/Sol/Earth/Moon", "Sol/Saturn", "world/Sol/Saturn", "Independent wire α", "marker", "tail");
        using (var geometry = new MemoryStream())
        {
            using (var writer = new BinaryWriter(geometry, System.Text.Encoding.UTF8, true))
            {
                writer.Write(2u); V(writer, -20, -15, -100); V(writer, 20, -15, -100);
                V(writer, 0, 0, 0); V(writer, 0, 0, 0);
            }
            fixture.Record(2, geometry.ToArray());
        }
        fixture.Record(5, Field(0)); fixture.Record(5, Field(1));
        byte[] earth = Body(0, 1, 0, 10, 12);
        byte[] moon = Body(2, 3, 25, 5, 0);
        byte[] saturn = Body(4, 5, -25, 8, 0);
        F(saturn, 52, 11); F(saturn, 56, 18);
        byte[] bodies = earth.Concat(moon).Concat(saturn).ToArray();
        for (int index = 1; index <= 30; index++)
        {
            byte[] frame = fixture.Frame(bodies, index <= 15 ? Overlays() : null, sequence: index);
            H(frame, 98, 3);
            if (index <= 15)
                foreach (int offset in new[] { 100, 102, 104, 106, 108, 110 }) H(frame, offset, 1);
            F(frame, 85, -1000); F(frame, 89, 0); // Sun below the saved ground zenith.
            fixture.Record(3, frame);
        }
        string file = Path.Combine(directory, "independent-three-body.celframe");
        File.WriteAllBytes(file, fixture.File());
        if (!scene.StartReplay(file)) throw new InvalidOperationException("The independently authored recording did not open.");
        await Drawn();
        var renderer = scene.Renderer(0);
        if (renderer.ArchivedGeometry?.Bodies.Count != 3 ||
            renderer.ArchivedBody("Sol/Earth") is not { Visible: true, MaterialOverride: ShaderMaterial earthMaterial } ||
            renderer.ArchivedBody("Sol/Earth/Moon") is not { Visible: true, MaterialOverride: ShaderMaterial moonMaterial } ||
            renderer.ArchivedBody("Sol/Saturn") is not { Visible: true } ||
            renderer.ArchivedRings(PlanetCatalogue.SaturnId) is not { Visible: true } ||
            renderer.ArchivedEarthLimb is not { Visible: true })
            throw new InvalidOperationException("Archived Earth, Moon, Saturn, rings and Earth limb must all be rendered.");
        if (!earthMaterial.GetShaderParameter("has_map").AsBool() ||
            !earthMaterial.GetShaderParameter("has_night_map").AsBool() ||
            !earthMaterial.GetShaderParameter("has_cloud_map").AsBool() ||
            !moonMaterial.GetShaderParameter("has_map").AsBool())
            throw new InvalidOperationException("Archived surfaces must retain their real day/night/cloud/lunar maps.");
        if (renderer.ArchivedOrbitSurfaces != 1 || renderer.ArchivedLabels.Count != 1 ||
            renderer.ArchivedLabels[0].Text != "Independent wire α" || renderer.ArchivedStarCount != 2 ||
            renderer.ArchivedPoint("Sol/Earth") is not { Visible: true } || renderer.ArchivedCometCount != 1 ||
            renderer.ArchivedGroundGlow is not { Visible: true })
            throw new InvalidOperationException("The independent recorded orbit, label, body point, comet and both star fields must be rendered.");
        using (var image = renderer.GetViewport().GetTexture().GetImage())
        {
            int lit = 0;
            for (int y = 0; y < image.GetHeight(); y += 4)
            for (int x = 0; x < image.GetWidth(); x += 4)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.R + pixel.G + pixel.B > .3) lit++;
            }
            if (lit < 100) throw new InvalidOperationException($"Recorded bodies produced only {lit} lit GPU samples.");
            if (image.SavePng(Path.Combine(directory, "independent-three-body.png")) != Error.Ok)
                throw new IOException("Could not save independent archive acceptance.");
            GD.Print($"Independent archive GPU: three textured bodies, rings/limb, orbit/label, comet, two star fields and {lit} lit samples.");
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (scene.Replaying)
        {
            if (timer.Elapsed.TotalSeconds > CoverageCapture.TimeoutSeconds(10)) throw new TimeoutException("Replay did not complete.");
            await Drawn();
        }
        await Drawn();
        if (renderer.ArchivedGeometry is not null || renderer.ArchivedBody("Sol/Earth")?.Visible == true ||
            renderer.ArchivedRings(PlanetCatalogue.SaturnId)?.Visible == true || renderer.ArchivedEarthLimb?.Visible == true)
            throw new InvalidOperationException("Completing replay must release its visible archive geometry.");
        if (!scene.LoadScenario(previous)) throw new InvalidOperationException("Could not restore the original observers.");
        var restored = scene.Session.CaptureScenario(0);
        if (!restored.Views.SequenceEqual(original.Views) || restored.DeltaT != original.DeltaT)
            throw new InvalidOperationException("Archive acceptance must leave the original cameras, clock rate and DeltaT restored.");
        byte[] invalid = fixture.File(); invalid[^1] ^= 1;
        string corrupt = Path.Combine(directory, "corrupt.celframe"); File.WriteAllBytes(corrupt, invalid);
        if (scene.StartReplay(corrupt) || scene.Replaying ||
            !scene.Session.CaptureScenario(0).Views.SequenceEqual(restored.Views))
            throw new InvalidOperationException("A corrupt recording must fail without replacing the live workspace.");
        GD.Print("Independent archive replay: 30 frames completed, GPU resources hidden, corrupt CRC rejected and cameras/DeltaT restored.");

        byte[] Body(uint name, uint material, float x, float radius, float atmosphere)
        {
            byte[] body = fixture.Body();
            U(body, 0, name); U(body, 48, material); F(body, 4, x); F(body, 16, radius);
            F(body, 40, 0); F(body, 44, 1); F(body, 60, atmosphere);
            return body;
        }

        byte[] Field(byte slot)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(slot); writer.Write(1); writer.Write((byte)3);
            V(writer, 0, 0, -1); writer.Write(0f); writer.Write(new byte[] { 255, 192, 128 });
            writer.Write(10f); V(writer, 0, .25f, 0);
            return stream.ToArray();
        }

        byte[] Overlays()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(7u); V(writer, 0, 0, -1); writer.Write(.75f); // discrete star
            writer.Write(0u); writer.Write(0u); V(writer, 0, 0, 0); V(writer, 0, 0, 0); writer.Write(1f);
            writer.Write((byte)0); writer.Write((byte)1); V(writer, .25f, .5f, 1); writer.Write(.5f);
            writer.Write(0u); V(writer, 0, 0, -90); writer.Write(2f); // Earth shadow blocker
            writer.Write(0u); writer.Write(1f); V(writer, 1, .75f, .5f); writer.Write(.5f); writer.Write((byte)0);
            writer.Write(0u); writer.Write(6u); V(writer, 0, 15, -100); writer.Write((byte)3);
            writer.Write(.75f); writer.Write((byte)1); V(writer, 1, .5f, .25f); writer.Write(.01f);
            writer.Write(8u); V(writer, 30, 0, -100); V(writer, 1, 0, 0); V(writer, 0, 1, 0);
            writer.Write(10f); writer.Write(2f); writer.Write(.5f); V(writer, .5f, .75f, 1); writer.Write(.25f);
            writer.Write((byte)0); writer.Write((byte)1); // absent lens, present stellar view
            V(writer, 0, 0, 0); V(writer, 0, 0, 0);
            writer.Write(0f); writer.Write(20f); writer.Write(1f); writer.Write(1f);
            writer.Write((byte)1); V(writer, 0, 1, 0);
            writer.Write(.5f); writer.Write(.25f); writer.Write(.125f); V(writer, .001f, 0, 0); writer.Write(3f);
            V(writer, 0, 0, -1); // independent periodic position
            return stream.ToArray();
        }

        static void V(BinaryWriter writer, float x, float y, float z)
        { writer.Write(x); writer.Write(y); writer.Write(z); }

        async Task Drawn()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }
}
