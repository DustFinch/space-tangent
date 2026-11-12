// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed class CompactLensTables
{
    private Texture2D? _angles, _spectrum;
    internal Texture2D Angles => _angles ??= Create(SchwarzschildLens.DeflectionTable(), SchwarzschildLens.TableSize, Image.Format.Rf);
    internal Texture2D Spectrum => _spectrum ??= Create(BlackbodyColor.Table(), BlackbodyColor.TableSize, Image.Format.Rgbaf);

    private static ImageTexture Create(float[] values, int width, Image.Format format)
    {
        byte[] data = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, data, 0, data.Length);
        using var image = Image.CreateFromData(width, 1, false, format, data);
        return ImageTexture.CreateFromImage(image);
    }
}
