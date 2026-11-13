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
            await KeyPress(Key.Escape);
            await Until(() => view.FindChild("ScriptSound5", true, false) is null, "Stopping the decoded media");
            GD.Print("UI: decoded WebP and played local Ogg and MP3 on independent channels.");
        }
        GD.Print("UI: converted and played classic media in one view, stopped it with Escape, and rejected an outside path.");
    }
}
