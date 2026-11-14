// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private ScriptMarkerCanvas? _scriptMarkerCanvas;
    internal IReadOnlyList<(Vector2 Center, ScriptMarker Marker)> VisibleScriptMarkers =>
        _scriptMarkerCanvas?.Markers ?? [];

    internal void DrawScriptMarkers(ObserverView observer, WorldSnapshot snapshot,
        IReadOnlyDictionary<ObjectId, ScriptMarker> markers, DeltaTModel deltaT)
    {
        if (markers.Count == 0 || observer.Navigation is not { } pose || CatalogueWorld is not { } world)
        {
            _scriptMarkerCanvas?.Markers.Clear();
            _scriptMarkerCanvas?.Hide();
            return;
        }
        if (_scriptMarkerCanvas is null)
        {
            var layer = new CanvasLayer { Layer = 24 };
            _scriptMarkerCanvas = new();
            AddChild(layer);
            layer.AddChild(_scriptMarkerCanvas);
        }

        var canvas = _scriptMarkerCanvas;
        Rect2 bounds = GetViewport().GetVisibleRect();
        canvas.Markers.Clear();
        foreach (var (id, marker) in markers)
        {
            if (marker.Opacity > 0 && world.TryNavigationState(id, snapshot, out var state, deltaT) &&
                ProjectNavigationPoint(pose, state.PositionKilometers) is { } center &&
                bounds.Grow((float)marker.Size * .5f).HasPoint(center))
                canvas.Markers.Add((center, marker));
        }
        canvas.Markers.Sort((left, right) => left.Marker.Size.CompareTo(right.Marker.Size));
        canvas.Show();
        canvas.QueueRedraw();
    }
}

internal partial class ScriptMarkerCanvas : Node2D
{
    internal List<(Vector2 Center, ScriptMarker Marker)> Markers { get; } = [];

    public override void _Draw()
    {
        foreach (var (center, marker) in Markers)
        {
            var color = new Color((float)marker.Color.X, (float)marker.Color.Y, (float)marker.Color.Z, (float)marker.Opacity);
            float radius = (float)marker.Size * .5f;
            var horizontal = Vector2.Right * radius;
            var vertical = Vector2.Down * radius;
            switch (marker.Shape)
            {
                case ScriptMarkerShape.Disk when marker.Size == 1:
                    DrawRect(new(center.Floor(), Vector2.One), color);
                    break;
                case ScriptMarkerShape.Disk:
                    DrawCircle(center, radius, color, antialiased: true);
                    break;
                case ScriptMarkerShape.Circle:
                    DrawCircle(center, radius, color, filled: false, width: 1, antialiased: true);
                    break;
                case ScriptMarkerShape.Square:
                case ScriptMarkerShape.FilledSquare:
                    DrawRect(new(center - horizontal - vertical, (horizontal + vertical) * 2), color,
                        filled: marker.Shape == ScriptMarkerShape.FilledSquare);
                    break;
                case ScriptMarkerShape.Diamond:
                    DrawPolyline([center - vertical, center + horizontal, center + vertical, center - horizontal,
                        center - vertical], color, 1, true);
                    break;
                case ScriptMarkerShape.Triangle:
                    DrawPolyline([center - vertical, center + vertical + horizontal, center + vertical - horizontal,
                        center - vertical], color, 1, true);
                    break;
                case ScriptMarkerShape.Cross:
                    DrawLine(center - horizontal, center + horizontal, color);
                    DrawLine(center - vertical, center + vertical, color);
                    break;
            }
        }
    }
}
