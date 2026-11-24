// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

internal readonly record struct ProjectionScale(double ScreenAngle, double Field, double Zoom, CameraMode Mode);

internal sealed record ViewControls(PanelContainer Panel, SimulationViewport Renderer, Label Epoch,
    Button Play, OptionButton Mode, OptionButton Selection, LineEdit TimelineEpoch, HBoxContainer TerrainTools,
    LineEdit TerrainCoordinates, SpinBox FieldOfView, Button AdvanceHour, SubViewportContainer Canvas, MeasurementOverlay Measurements)
{
    internal Label? PointerReadout { get; set; }
    internal Vector2? PointerPosition { get; set; }
    internal WorldSnapshot? Snapshot { get; set; }
    internal SimulationWorld? SnapshotWorld { get; set; }
    internal ProjectionScale? Perspective { get; set; }
    internal bool DraggingCamera { get; set; }
    internal Vector2? LookPress { get; set; }
    internal int LookGesture { get; set; }
    internal bool Looking { get; set; }
    internal bool CameraCoastLooksAround { get; set; }
    internal Vector2 CameraDragVelocity { get; set; }
    internal double CameraDollyRate { get; set; }

    internal static string PlaybackButton(bool playing) => playing
        ? WorkspaceText.Choose("Pause", "Пауза")
        : WorkspaceText.Choose("Play", "Пуск");
}

