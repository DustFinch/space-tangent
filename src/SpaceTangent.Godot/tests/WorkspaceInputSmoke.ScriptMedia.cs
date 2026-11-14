// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseScriptMedia(string directory)
    {
        string image = Path.Combine(directory, "icon.png");
        using (var original = GD.Load<Texture2D>("res://UI/space-tangent.png").GetImage())
            Require(original.SavePng(image) == Error.Ok, "Copy the application icon for the media probe.");
        using (var wave = new BinaryWriter(File.Create(Path.Combine(directory, "silent.wav"))))
        {
            wave.Write("RIFF"u8);
            wave.Write(8036);
            wave.Write("WAVEfmt "u8);
            wave.Write(16);
            wave.Write((short)1);
            wave.Write((short)1);
            wave.Write(8000);
            wave.Write(16000);
            wave.Write((short)2);
            wave.Write((short)16);
            wave.Write("data"u8);
            wave.Write(8000);
            wave.Write(new byte[8000]);
        }

        string source = Path.Combine(directory, "media.cel");
        File.WriteAllText(source, """
            {
                overlay { filename "icon.png" duration 20 width 64 height 64 }
                play { filename "silent.wav" channel 3 loop true }
                wait { duration 20 }
            }
            """);
        var conversion = TourConverter.ConvertFile(source);
        Require(conversion.Commands == 3 && conversion.Unapplied.Count == 0, "All media commands must convert.");
        string convertedDirectory = Directory.CreateDirectory(Path.Combine(directory, "converted media")).FullName;
        string script = Path.Combine(convertedDirectory, "media.lua");
        ScriptMediaPlayer.CopyAssets(source, script, conversion.MediaFiles);
        Require(File.Exists(Path.Combine(convertedDirectory, "icon.png")) && File.Exists(Path.Combine(convertedDirectory, "silent.wav")),
            "Exporting the Lua file must preserve its local media.");
        File.WriteAllText(script, conversion.Lua);
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        var running = _scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the converted media script.");
        running.Step(0);
        Require(running.Error is null, running.Error ?? "");
        var view = _scene.Renderer(0);
        Require(view.FindChild("ScriptImage", true, false) is TextureRect { Texture: not null } &&
            view.FindChild("ScriptSound3", true, false) is AudioStreamPlayer { Playing: true },
            "The opened script must display the image and start the selected audio channel.");
        Require(_scene.Renderer(1).FindChild("ScriptImage", true, false) is null,
            "The script image must stay in its originating view.");
        if (DisplayServer.GetName() != "headless")
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var overlay = view.GetViewport().GetTexture().GetImage();
            await KeyPress(Key.Escape);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var cleared = view.GetViewport().GetTexture().GetImage();
            int changed = 0;
            for (int y = overlay.GetHeight() / 2 - 24; y < overlay.GetHeight() / 2 + 24; y++)
            for (int x = overlay.GetWidth() / 2 - 24; x < overlay.GetWidth() / 2 + 24; x++)
                if (overlay.GetPixel(x, y) != cleared.GetPixel(x, y))
                    changed++;
            Require(changed > 100, "The media image must change pixels in the originating viewport.");
            GD.Print($"GPU: script media changed {changed} sampled viewport pixels.");
        }
        else
            await KeyPress(Key.Escape);
        await Until(() => view.FindChild("ScriptImage", true, false) is null &&
            view.FindChild("ScriptSound3", true, false) is null, "Stopping the script media");

        File.WriteAllText(script, "st.overlay('../icon.png')");
        running = _scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the path check.");
        running.Step(0);
        Require(running.Error?.Contains("relative to the script folder", StringComparison.Ordinal) == true,
            "The media request must stay in the script folder.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MEDIA_FORMATS") is { Length: > 0 } formats)
        {
            string[] audioNames = ["collect.ogg", "collect.mp3"];
            foreach (string name in audioNames)
                File.Copy(Path.Combine(formats, name), Path.Combine(convertedDirectory, name));
            using (var original = GD.Load<Texture2D>("res://UI/space-tangent.png").GetImage())
                Require(original.SaveWebp(Path.Combine(convertedDirectory, "icon.webp")) == Error.Ok, "Prepare a WebP image.");
            File.WriteAllText(script, """
                st.overlay('icon.webp', 10, 0.5, 0.5, 48, 48)
                st.play('collect.ogg', 1, 4, true)
                st.play('collect.mp3', 1, 5, true)
                st.wait(20)
                """);
            running = _scene.RunScript(script) ?? throw new InvalidOperationException("Could not open the media format check.");
            running.Step(0);
            Require(running.Error is null, running.Error ?? "");
            Require(view.FindChild("ScriptImage", true, false) is TextureRect { Texture: not null } &&
                view.FindChild("ScriptSound4", true, false) is AudioStreamPlayer { Playing: true, Stream: AudioStreamOggVorbis } &&
                view.FindChild("ScriptSound5", true, false) is AudioStreamPlayer { Playing: true, Stream: AudioStreamMP3 },
                "WebP, Ogg and MP3 must load using their own decoders.");
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MEDIA_MIXER_PROBE") == "1")
                await VerifyDecodedAudio(view);
            await KeyPress(Key.Escape);
            await Until(() => view.FindChild("ScriptSound5", true, false) is null, "Stopping the decoded media");
            GD.Print("UI: decoded WebP and played local Ogg and MP3 on independent channels.");
        }
        GD.Print("UI: converted and played classic media in one view, stopped it with Escape, and rejected an outside path.");
        if (DisplayServer.GetName() != "headless")
            await ExerciseScriptCapture(directory, source, script);
    }

    private async Task ExerciseScriptCapture(string directory, string source, string script)
    {
        string? oldOutput = System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT");
        string output = Path.Combine(directory, "script screenshots");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT", output);
        try
        {
            File.WriteAllText(source, """
                {
                    capture { filename "classic moon" }
                    capture {}
                    exit {}
                }
                """);
            var converted = TourConverter.ConvertFile(source);
            Require(converted.Unapplied.Count == 0 && converted.MediaFiles.Count == 0,
                "Screenshots are outputs and must not be copied with the tour inputs.");
            File.WriteAllText(script, converted.Lua);
            Require(_scene.RunScript(script) is not null, "Open the classic screenshot tour.");
            string named = Path.Combine(output, "classic moon.png");
            await Until(() => Directory.Exists(output) && Directory.GetFiles(output, "*.png").Length == 2,
                "Saving both tour screenshots");
            using var image = Image.LoadFromFile(named);
            Require(!image.IsEmpty() && (Vector2)image.GetSize() == _scene.GetViewport().GetVisibleRect().Size,
                "The named screenshot must contain the rendered application viewport.");

            byte[] existing = [1, 2, 3, 4];
            File.WriteAllBytes(named, existing);
            File.WriteAllText(script, "st.capture('classic moon.png')");
            Require(_scene.RunScript(script) is not null, "Open the duplicate filename check.");
            for (int frame = 0; frame < 3; frame++)
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Require(File.ReadAllBytes(named).SequenceEqual(existing), "An existing capture must not be overwritten.");

            string outside = Path.Combine(directory, "outside.png");
            File.WriteAllText(script, "st.capture('../outside.png')");
            var running = _scene.RunScript(script) ?? throw new InvalidOperationException("Open the screenshot path check.");
            running.Step(0);
            Require(running.Error?.Contains("plain filename", StringComparison.Ordinal) == true && !File.Exists(outside),
                "A script capture must reject paths outside the capture folder.");
            GD.Print("GPU: classic tour saved named and automatic PNGs and rejected an outside capture path.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT", oldOutput);
        }
    }

    private async Task VerifyDecodedAudio(SimulationViewport view)
    {
        using var capture = new AudioEffectCapture { BufferLength = .5f };
        int effect = AudioServer.GetBusEffectCount(0);
        AudioServer.AddBusEffect(0, capture);
        var ogg = (AudioStreamPlayer)view.FindChild("ScriptSound4", true, false);
        var mp3 = (AudioStreamPlayer)view.FindChild("ScriptSound5", true, false);
        try
        {
            ogg.Stop();
            mp3.Stop();
            float oggPeak = await Peak(ogg);
            float mp3Peak = await Peak(mp3);
            GD.Print(FormattableString.Invariant($"Script audio mixer: Ogg PCM peak {oggPeak:F4}, MP3 PCM peak {mp3Peak:F4}."));
        }
        finally
        {
            AudioServer.RemoveBusEffect(0, effect);
        }

        async Task<float> Peak(AudioStreamPlayer player)
        {
            capture.ClearBuffer();
            player.Play();
            float peak = 0;
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (started.Elapsed.TotalSeconds < 3 && peak <= .001f)
                {
                    await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
                    while (capture.CanGetBuffer(256))
                        foreach (Vector2 sample in capture.GetBuffer(256))
                            peak = Math.Max(peak, Math.Max(Math.Abs(sample.X), Math.Abs(sample.Y)));
                }
                Require(peak > .001f, $"{player.Name} must produce non-silent stereo PCM.");
                return peak;
            }
            finally { player.Stop(); }
        }
    }
}
