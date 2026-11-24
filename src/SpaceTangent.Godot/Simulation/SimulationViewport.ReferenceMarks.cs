// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private ReferenceMarkRendering? _referenceMarkRendering;
    internal bool ReferenceMarksVisible => _renderResourcesClosed ? false : (_referenceMarkRendering?.Visible == true);
    internal void DrawReferenceMarks(ObserverView view, BodyReferenceGeometry? sample)
    {
        if (_renderResourcesClosed) return;
        _referenceMarkRendering?.Hide();
        if (view.Navigation is not { } pose || sample is not { } geometry) return;
        var flags = ReferenceMarkRendering.CompatibleFlags(view.ReferenceMarks, geometry);
        if (flags == BodyReferenceMarks.None) return;
        double units = _navigationUnits;
        var center = ProjectNavigation((geometry.Center - pose.AnchorKilometers) - _navigationEyeOffset, units, out double projection);
        (_referenceMarkRendering ??= new(this)).Draw(flags, geometry, center, projection / units);
    }
}
