// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

/// <summary>
/// Owns the input stream while distinguishing an archived collection from one
/// saved observer. The caller can close replay after classification and before
/// parsing an observer, preserving its existing transition policy.
/// </summary>
public sealed class ObserverWorkspaceReader : IDisposable
{
    private readonly string _path;
    private readonly FileStream? _archive;
    private bool _disposed;
    public bool IsFavorites { get; }

    public ObserverWorkspaceReader(string path)
    {
        _path = path;
        if (!Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) return;
        _archive = File.OpenRead(path);
        try { IsFavorites = ObserverBookmarkFile.IsArchivedCollection(_archive); }
        catch { _archive.Dispose(); throw; }
    }

    public SimulationScenario ReadScenario(SimulationWorld world, DeltaTModel model)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFavorites) throw new InvalidOperationException("The input contains favorites rather than one observer.");
        if (_archive is null) return ScenarioFile.Load(_path);
        _archive.Position = 0;
        return SceneLink.ReadArchivedObserver(_archive, world, model);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _archive?.Dispose();
    }
}
