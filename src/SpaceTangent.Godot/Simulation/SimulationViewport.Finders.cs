// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private FinderOverlay? _finderOverlay;
    internal bool FindersVisible => _finderOverlay?.Visible == true;

    private void DrawFinders(ObserverView observer)
    {
        if (!observer.SupportsOptics || observer.ActiveOptics is not null ||
            observer.Optics is not { Finders: { Visible: true } } optics)
        {
            _finderOverlay?.Hide();
            return;
        }
        if (_finderOverlay is null)
        {
            var layer = new CanvasLayer { Layer = 23 };
            AddChild(layer);
            _finderOverlay = new();
            layer.AddChild(_finderOverlay);
            _finderOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _finderOverlay.ShowFrame(optics, observer.EffectiveFieldOfView);
    }
}
