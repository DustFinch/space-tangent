// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed class GalacticTextureCache : IDisposable
{
    private const int Width = 512, Height = 256;
    private const int Capacity = 18;
    private readonly Dictionary<(Vector3d Center, int Neighbours), (ImageTexture Texture, long Use)> _maps = new();
    private CancellationTokenSource? _bake;
    private GalacticDustGrid? _dust;
    private bool _disposed;
    private long _use;
    public event Action? Published;

    public bool HasDust => _dust is not null;

    public void SetDust(GalacticDustGrid? dust)
    {
        if (ReferenceEquals(_dust, dust))
            return;
        _dust = dust;
        _bake?.Cancel();
        _maps.Clear();
    }

    public (Texture2D? Texture, Vector3d Center) Request(Vector3d position, int neighbours)
    {
        const double scale = 8;
        Vector3d center = (position - GalacticLight.SunKiloparsecs).Length <= .05 ? GalacticLight.SunKiloparsecs :
            new(Math.Round(position.X * scale) / scale, Math.Round(position.Y * scale) / scale,
                Math.Round(position.Z * scale) / scale);
        var key = (center, neighbours);
        if (_maps.TryGetValue(key, out var entry))
        {
            _maps[key] = (entry.Texture, ++_use);
            return (entry.Texture, center);
        }
        if (_bake is null && !_disposed)
        {
            var pending = new CancellationTokenSource();
            _bake = pending;
            _ = Bake(key, _dust, pending);
        }
        return (null, center);
    }

    private async Task Bake((Vector3d Center, int Neighbours) key, GalacticDustGrid? dust, CancellationTokenSource pending)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            int workers = Math.Clamp(System.Environment.ProcessorCount / 4, 1, 6);
            float[] pixels = await Task.Run(() => GalacticLight.Bake(key.Center, Width, Height, workers, dust,
                pending.Token, key.Neighbours), pending.Token);
            if (!pending.IsCancellationRequested && !_disposed)
            {
                byte[] bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
                using Image image = Image.CreateFromData(Width, Height, false, Image.Format.Rgbf, bytes);
                if (_maps.Count >= Capacity)
                    _maps.Remove(_maps.MinBy(entry => entry.Value.Use).Key);
                _maps[key] = (ImageTexture.CreateFromImage(image), ++_use);
                GD.Print($"Galactic light map ready at {key.Center} kpc in {elapsed.ElapsedMilliseconds} ms.");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _bake = null;
            pending.Dispose();
        }
        if (!_disposed)
            Published?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _bake?.Cancel();
        _maps.Clear();
    }
}
