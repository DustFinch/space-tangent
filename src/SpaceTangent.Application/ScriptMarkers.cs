// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public enum ScriptMarkerShape { Disk, Circle, Square, FilledSquare, Diamond, Triangle, Cross }

public readonly record struct ScriptMarker(Vector3d Color, double Size, double Opacity)
{
    public ScriptMarkerShape Shape { get; init; }
}

public sealed partial class SimulationSession
{
    internal const int MaximumScriptMarkers = 100_000;
    private static readonly IReadOnlyDictionary<ObjectId, ScriptMarker> EmptyMarkers =
        new System.Collections.ObjectModel.ReadOnlyDictionary<ObjectId, ScriptMarker>(new Dictionary<ObjectId, ScriptMarker>());
    private readonly Dictionary<int, MarkerLayer> _scriptMarkers = [];

    private sealed class MarkerLayer
    {
        internal readonly Dictionary<ObjectId, ScriptMarker> Items = [];
        internal bool Visible;
    }

    public IReadOnlyDictionary<ObjectId, ScriptMarker> Markers(int view) =>
        _scriptMarkers.TryGetValue(_viewIds[view], out var layer) && layer.Visible ? layer.Items : EmptyMarkers;

    internal void SetScriptMarkersVisible(int view, bool visible) => ScriptMarkerLayer(view).Visible = visible;
    internal void ClearScriptMarkers(int view) => ScriptMarkerLayer(view).Items.Clear();
    internal void RemoveScriptMarker(int view, ObjectId id) => ScriptMarkerLayer(view).Items.Remove(id);

    internal void PutScriptMarker(int view, ObjectId id, ScriptMarker marker)
    {
        var items = ScriptMarkerLayer(view).Items;
        if (items.Count >= MaximumScriptMarkers && !items.ContainsKey(id))
            throw new InvalidOperationException("Script marker limit exceeded.");
        items[id] = marker;
    }

    private MarkerLayer ScriptMarkerLayer(int view)
    {
        int id = _viewIds[view];
        if (!_scriptMarkers.TryGetValue(id, out var layer))
            _scriptMarkers.Add(id, layer = new());
        return layer;
    }
}
