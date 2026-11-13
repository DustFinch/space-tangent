// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;

namespace SpaceTangent.Godot.Simulation;

internal static class ZodiacalGlow
{
    private const double Obliquity = 23.4392911 * Math.PI / 180;

    internal static Texture2D? LoadMap()
    {
        string path = LocalDataPaths.Find("SPACETANGENT_ZODIACAL_MAP", "sky/zodiacal-light.png");
        if (!File.Exists(path) || DisplayServer.GetName() == "headless")
            return null;
        try
        {
            using var source = File.OpenRead(path);
            if (source.Length is < 24 or > 1024 * 1024)
                throw new InvalidDataException("Invalid zodiacal-light map size.");
            var bytes = new byte[(int)source.Length];
            source.ReadExactly(bytes);
            if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)) != 512 ||
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)) != 256)
                throw new InvalidDataException("Expected a 512 × 256 zodiacal-light PNG.");
            using var image = new Image();
            if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.GetWidth() != 512 || image.GetHeight() != 256)
                throw new InvalidDataException("Could not decode the zodiacal-light map.");
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.Print("Zodiacal light: " + error.Message);
            return null;
        }
    }

    internal static bool Configure(ShaderMaterial material, bool enabled, Vector3d sun, float darkness = 1)
    {
        double au = sun.Length / AstronomicalUnits.KilometersPerAstronomicalUnit;
        bool visible = enabled && au is > .7 and < 1.3 && darkness > 0;
        material.SetShaderParameter("show_zodiacal_light", visible);
        if (!visible)
            return false;
        double eclipticY = sun.Y * Math.Cos(Obliquity) + sun.Z * Math.Sin(Obliquity);
        material.SetShaderParameter("zodiacal_sun_longitude", (float)Math.Atan2(eclipticY, sun.X));
        material.SetShaderParameter("zodiacal_strength", .06f * darkness);
        return true;
    }
}
