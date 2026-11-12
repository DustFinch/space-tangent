// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using Godot;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed record LandscapeLayerPixels(PanoramaBounds Bounds, PanoramaPixels Pixels);
internal sealed record PreparedLandscape(ObservingLandscape Definition,
    LandscapeLayerPixels Ground, LandscapeLayerPixels? Lights, LandscapeLayerPixels? Fog);
internal sealed record LandscapeTexture(PanoramaBounds Bounds, Texture2D Texture);
internal sealed record LandscapeTextures(string Name, LandscapeTexture Ground, LandscapeTexture? Lights, LandscapeTexture? Fog);

internal static class LandscapeResources
{
    internal static PreparedLandscape Read(string root, string id, CancellationToken cancellation)
    {
        if (!ObservingLandscape.ValidId(id))
            throw new InvalidDataException("The landscape identifier is invalid.");
        string directory = Path.Combine(root, id);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A landscape directory cannot be a symbolic link.");
        string ini = Asset("landscape.ini");
        if (new FileInfo(ini).Length > 64 * 1024)
            throw new InvalidDataException("Landscape definition exceeds its size limit.");
        using var reader = File.OpenText(ini);
        var definition = ObservingLandscape.Read(id, reader);
        if (!definition.Body.Equals("Earth", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The panorama is for {definition.Body}.");
        return new(definition, Layer(LandscapeLayer.Ground) ?? throw new InvalidDataException("Landscape ground is missing."),
            Layer(LandscapeLayer.Lights), Layer(LandscapeLayer.Fog));

        LandscapeLayerPixels? Layer(LandscapeLayer layer)
        {
            cancellation.ThrowIfCancellationRequested();
            PanoramaPixels? pixels = definition.Projection == LandscapeProjection.Spherical
                ? definition.File(layer) is { } image ? ImagePixels(image) : null
                : PanoramaBaker.Bake(definition, layer, ImagePixels, ReadText, cancellation: cancellation);
            return pixels is null ? null : new(definition.Bounds(layer), pixels);
        }

        string? ReadText(string filename)
        {
            string path = Asset(filename);
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            if (file.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Landscape horizon exceeds its size limit.");
            return File.ReadAllText(path);
        }

        PanoramaPixels? ImagePixels(string filename)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Asset(filename);
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            if (stream.Length is < 24 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Landscape texture exceeds its size limit.");
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt64BigEndian(header) != 0x89504E470D0A1A0A || !header[12..16].SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Landscape texture needs a PNG header.");
            int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 16_777_216)
                throw new InvalidDataException("Landscape texture exceeds its pixel limit.");
            byte[] data = new byte[(int)stream.Length];
            header.CopyTo(data);
            stream.ReadExactly(data.AsSpan(header.Length));
            using var decoded = new Image();
            if (decoded.LoadPngFromBuffer(data) != Error.Ok || decoded.GetWidth() != width || decoded.GetHeight() != height)
                throw new InvalidDataException("Landscape PNG could not be decoded.");
            decoded.Convert(Image.Format.Rgba8);
            return new(width, height, decoded.GetData());
        }

        string Asset(string filename)
        {
            if (Path.GetFileName(filename) != filename || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || filename is "." or "..")
                throw new InvalidDataException("Landscape assets need local filenames.");
            string path = Path.Combine(directory, filename);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A landscape asset cannot be a symbolic link.");
            return path;
        }
    }

    internal static LandscapeTextures Publish(PreparedLandscape prepared) =>
        new(prepared.Definition.Name, Upload(prepared.Ground), prepared.Lights is { } lights ? Upload(lights) : null,
            prepared.Fog is { } fog ? Upload(fog) : null);

    private static LandscapeTexture Upload(LandscapeLayerPixels layer)
    {
        PanoramaPixels pixels = layer.Pixels;
        using var image = Image.CreateFromData(pixels.Width, pixels.Height, false, Image.Format.Rgba8, pixels.Rgba);
        image.GenerateMipmaps();
        return new(layer.Bounds, ImageTexture.CreateFromImage(image));
    }
}
