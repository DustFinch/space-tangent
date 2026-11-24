// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private OpticalRendering? _opticalRendering;
    private OpticalRendering Optics => _opticalRendering ??= new(this, frame => Draw(frame.Observer, frame.Snapshot, frame.DeltaT));
    internal bool EyepieceVisible => _opticalRendering?.EyepieceVisible == true;
    public override void _Process(double delta) => _opticalRendering?.Process(delta);
    private double OpticalField(ObserverView observer) => Optics.Field(observer);
    private void DrawEyepiece(ObserverView observer) => Optics.Draw(observer);
    private bool InsideEyepiece(Vector2 position) => _opticalRendering?.Contains(position) != false;
}
