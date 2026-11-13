// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal sealed class ScriptMediaPlayer(SimulationViewport renderer)
{
    private const int MaximumAssetBytes = 16 * 1024 * 1024;
    private readonly Dictionary<int, AudioStreamPlayer> _audio = [];
    private readonly Dictionary<TextureRect, ImageTexture> _images = [];
    private CanvasLayer? _layer;
    internal bool Attached => GodotObject.IsInstanceValid(renderer) && renderer.IsInsideTree();
    internal bool Active => _images.Count > 0 || _audio.Count > 0;

    internal void Present(string directory, ScriptMediaRequest request)
    {
        if (request is ScriptMediaRequest.Sound { File.Length: 0 } stop)
        {
            StopSound(stop.Channel);
            return;
        }

        if (!Attached)
            throw new InvalidOperationException("The script media view is closed.");
        string asset = ResolveAsset(directory, request.File);
        using var file = File.OpenRead(asset);
        if (file.Length is <= 0 or > MaximumAssetBytes)
            throw new InvalidDataException("A script media file must be between 1 byte and 16 MiB.");
        byte[] data = new byte[(int)file.Length];
        file.ReadExactly(data);
        string extension = Path.GetExtension(asset).ToLowerInvariant();

        if (request is ScriptMediaRequest.Sound sound)
        {
            AudioStream? stream = extension switch
            {
                ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(data),
                ".mp3" => AudioStreamMP3.LoadFromBuffer(data),
                ".wav" => AudioStreamWav.LoadFromBuffer(data),
                _ => throw new InvalidDataException("Use an Ogg, MP3 or WAV script sound.")
            };
            if (stream is null)
                throw new InvalidDataException("Could not decode the script sound.");
            StopSound(sound.Channel);
            var player = new AudioStreamPlayer { Name = $"ScriptSound{sound.Channel}", Stream = stream, VolumeDb = Mathf.LinearToDb((float)Math.Max(.00001, sound.Volume)) };
            renderer.AddChild(player);
            _audio.Add(sound.Channel, player);
            player.Finished += () =>
            {
                if (_audio.GetValueOrDefault(sound.Channel) != player)
                    return;
                if (sound.Loop)
                    player.Play();
                else
                    StopSound(sound.Channel);
            };
            player.Play();
            return;
        }

        if (request is not ScriptMediaRequest.Overlay overlay)
            return;
        if (_images.Count >= 4)
            throw new InvalidDataException("A script can display at most four images at once.");
        using var image = new Image();
        Error error = extension switch
        {
            ".png" => image.LoadPngFromBuffer(data),
            ".jpg" or ".jpeg" => image.LoadJpgFromBuffer(data),
            ".webp" => image.LoadWebpFromBuffer(data),
            _ => throw new InvalidDataException("Use a PNG, JPEG or WebP script image.")
        };
        if (error != Error.Ok || image.IsEmpty() || image.GetWidth() > 4096 || image.GetHeight() > 4096)
            throw new InvalidDataException("The script image is unreadable or exceeds 4096 by 4096 pixels.");
        if (_layer is null)
        {
            _layer = new CanvasLayer { Layer = 50 };
            renderer.AddChild(_layer);
        }
        var texture = ImageTexture.CreateFromImage(image);
        var size = new Vector2(overlay.Width > 0 ? (float)overlay.Width : image.GetWidth(),
            overlay.Height > 0 ? (float)overlay.Height : image.GetHeight());
        var position = new Vector2((float)overlay.X, (float)overlay.Y) * renderer.GetViewport().GetVisibleRect().Size;
        var picture = new TextureRect
        {
            Name = "ScriptImage", Texture = texture, Size = size, Position = position - size / 2,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, (float)overlay.Opacity)
        };
        _layer.AddChild(picture);
        _images.Add(picture, texture);
        _ = Expire(picture, overlay.Seconds);
    }

    private async Task Expire(TextureRect picture, double seconds)
    {
        await renderer.ToSignal(renderer.GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        if (_images.Remove(picture, out var texture))
        {
            if (GodotObject.IsInstanceValid(picture))
                picture.QueueFree();
            texture.Dispose();
        }
    }

    internal void Clear()
    {
        foreach (int channel in _audio.Keys.ToArray())
            StopSound(channel);
        foreach (var (picture, texture) in _images)
        {
            if (GodotObject.IsInstanceValid(picture))
                picture.QueueFree();
            texture.Dispose();
        }
        _images.Clear();
        if (GodotObject.IsInstanceValid(_layer))
            _layer!.QueueFree();
        _layer = null;
    }

    private void StopSound(int channel)
    {
        if (!_audio.Remove(channel, out var player))
            return;
        if (GodotObject.IsInstanceValid(player))
        {
            player.Stop();
            player.Stream?.Dispose();
            player.QueueFree();
        }
    }

    internal static void CopyAssets(string source, string destination, IReadOnlyList<string> files)
    {
        if (files.Count > 32)
            throw new InvalidDataException("A converted script can reference at most 32 media files.");
        string sourceRoot = Path.GetDirectoryName(Path.GetFullPath(source))!;
        string destinationRoot = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        foreach (string name in files)
        {
            string input = ResolveAsset(sourceRoot, name);
            string output = ResolveAsset(destinationRoot, name, createParents: true);
            if (input.Equals(output, StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = File.OpenRead(input);
            if (stream.Length is <= 0 or > MaximumAssetBytes)
                throw new InvalidDataException("A script media file must be between 1 byte and 16 MiB.");
            if (File.Exists(output))
            {
                using var existing = File.OpenRead(output);
                if (stream.Length != existing.Length || !System.Security.Cryptography.SHA256.HashData(stream)
                        .AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(existing)))
                    throw new IOException("A different media file already exists at the destination.");
                continue;
            }
            using var target = new FileStream(output, FileMode.CreateNew, System.IO.FileAccess.Write);
            stream.CopyTo(target);
        }
    }

    private static string ResolveAsset(string directory, string relative, bool createParents = false)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (relative.Length > 512 || relative.Contains(':') || parts.Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Script media paths must be relative to the script folder.");
        string path = directory;
        for (int index = 0; index < parts.Length; index++)
        {
            path = Path.Combine(path, parts[index]);
            if (createParents && !Path.Exists(path))
            {
                if (index < parts.Length - 1)
                    Directory.CreateDirectory(path);
                else
                    break;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Script media paths cannot contain symbolic links.");
        }
        return path;
    }
}
