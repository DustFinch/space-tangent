// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private readonly CancellationTokenSource _landscapeLifetime = new();
    private readonly SemaphoreSlim _landscapeDecoder = new(1);
    private readonly Dictionary<string, LandscapeTextures?> _landscapeTextures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _landscapesLoading = new(StringComparer.Ordinal);
    private string? _landscapeRoot;
    private string LandscapeRoot => _landscapeRoot ??= LocalDataPaths.Find("SPACETANGENT_LANDSCAPES", "landscapes", directory: true);

    private void CycleLandscape(bool clear)
    {
        var current = Session![_active];
        if (current.Mode != CameraMode.Surface || current.Navigation is not null)
        {
            _status.Text = "Panoramas apply in the Earth surface view.";
            return;
        }
        if (clear)
        {
            SetLandscapeChoice(_active, null);
            return;
        }
        try
        {
            var choices = Directory.Exists(LandscapeRoot)
                ? Directory.EnumerateDirectories(LandscapeRoot).Select(path => Path.GetFileName(path))
                    .Where(id => ObservingLandscape.ValidId(id) && HasEarthDefinition(id)).Order(StringComparer.Ordinal).ToArray()
                : [];
            if (choices.Length == 0)
            {
                _status.Text = "No Earth panoramas in the local landscapes directory.";
                return;
            }
            int currentIndex = Array.IndexOf(choices, current.EarthObserver?.LandscapeId);
            SetLandscapeChoice(_active, choices[(currentIndex + 1) % choices.Length]);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = "Could not read local landscapes. " + error.Message;
        }
    }

    private bool HasEarthDefinition(string id)
    {
        string directory = Path.Combine(LandscapeRoot, id);
        string path = Path.Combine(directory, "landscape.ini");
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024 ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return false;
        try
        {
            using var reader = File.OpenText(path);
            return ObservingLandscape.Read(id, reader).Body.Equals("Earth", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException) { return false; }
    }

    internal void SetLandscapeChoice(int view, string? id)
    {
        var session = Session!;
        var camera = session[view].EarthObserver ?? EarthObserverCamera.Moscow;
        if (id is not null && _landscapeTextures.TryGetValue(id, out var attempted) && attempted is null)
            _landscapeTextures.Remove(id);
        session.SetEarthObserver(view, camera with { LandscapeId = id });
        _status.Text = id is null ? "Local panorama hidden." : "Landscape · " + id;
        Refresh();
    }

    private LandscapeTextures? ViewLandscape(ObserverView observer)
    {
        if (observer.Mode != CameraMode.Surface || observer.Navigation is not null ||
            observer.EarthObserver?.LandscapeId is not { } id)
            return null;
        if (_landscapeTextures.TryGetValue(id, out var textures))
            return textures;
        if (_landscapesLoading.Add(id))
            _ = PrepareLandscape(id);
        return null;
    }

    private async Task PrepareLandscape(string id)
    {
        var token = _landscapeLifetime.Token;
        try
        {
            await _landscapeDecoder.WaitAsync(token);
            PreparedLandscape decoded;
            try
            {
                string root = LandscapeRoot;
                decoded = await Task.Run(() => LandscapeResources.Read(root, id, token), token);
            }
            finally { _landscapeDecoder.Release(); }
            if (token.IsCancellationRequested || !IsInsideTree())
                return;
            var textures = LandscapeResources.Publish(decoded);
            if (_landscapeTextures.Count >= 12)
            {
                var unused = _landscapeTextures.Keys.FirstOrDefault(candidate =>
                    Enumerable.Range(0, Session!.ViewCount).All(index => Session[index].EarthObserver?.LandscapeId != candidate));
                if (unused is not null)
                    _landscapeTextures.Remove(unused);
            }
            _landscapeTextures[id] = textures;
            Refresh();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (IsLoadError(error))
        {
            if (!token.IsCancellationRequested && IsInsideTree())
            {
                _landscapeTextures[id] = null;
                if (Session![_active].EarthObserver?.LandscapeId == id)
                    _status.Text = $"Landscape {id} is unavailable. {error.Message}";
            }
        }
        finally { _landscapesLoading.Remove(id); }
    }
}
