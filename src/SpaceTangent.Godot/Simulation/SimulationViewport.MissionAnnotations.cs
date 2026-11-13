// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MissionAnnotationOverlay? _missionAnnotationOverlay;
    internal IReadOnlyList<string> VisibleMissionAnnotations => _missionAnnotationOverlay?.VisibleText ?? [];

    internal void SetMissionAnnotations(IReadOnlyList<MissionAnnotation> annotations, JulianInstant epoch)
    {
        if (_missionAnnotationOverlay is null)
        {
            if (annotations.Count == 0)
                return;
            var layer = new CanvasLayer { Layer = 25 };
            AddChild(layer);
            _missionAnnotationOverlay = new();
            layer.AddChild(_missionAnnotationOverlay);
            GetViewport().SizeChanged += _missionAnnotationOverlay.QueueRedraw;
        }
        _missionAnnotationOverlay.Update(annotations, epoch);
    }
}

internal partial class MissionAnnotationOverlay : Control
{
    private readonly List<(MissionAnnotation Track, MissionAnnotationLine Line)> _visible = [];
    private IReadOnlyList<MissionAnnotation>? _tracks;
    private JulianInstant _epoch;

    internal MissionAnnotationOverlay() => MouseFilter = MouseFilterEnum.Ignore;
    internal IReadOnlyList<string> VisibleText => _visible.Select(item => item.Line.Text).ToArray();

    internal void Update(IReadOnlyList<MissionAnnotation> annotations, JulianInstant epoch)
    {
        if (ReferenceEquals(_tracks, annotations) && _epoch == epoch)
            return;
        _tracks = annotations;
        _epoch = epoch;
        bool hadText = _visible.Count > 0;
        _visible.Clear();
        foreach (var annotation in annotations)
        foreach (var line in annotation.Lines)
            if (line.Text.Length != 0 && line.VisibleAt(epoch))
                _visible.Add((annotation, line));
        if (hadText || _visible.Count > 0)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var font = GetThemeDefaultFont();
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        foreach (var (track, line) in _visible)
        {
            int size = track.Size switch { "small" => 13, "large" => 22, _ => 17 };
            Vector3d color = line.Color ?? track.Color;
            float width = font.GetStringSize(line.Text, fontSize: size).X;
            float shift = track.Alignment switch { "center" or "centre" => width / 2, "right" => width, _ => 0 };
            Vector2 at = new((float)(track.X * viewport.X) - shift,
                (float)((1 - track.Y) * viewport.Y) + line.Line * size * 1.35f);
            DrawString(font, at + Vector2.One, line.Text, fontSize: size, modulate: new(0, 0, 0, .6f));
            DrawString(font, at, line.Text, fontSize: size, modulate: new((float)color.X, (float)color.Y, (float)color.Z));
        }
    }
}
