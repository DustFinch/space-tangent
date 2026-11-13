// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private CanvasLayer? _nightFilter;

    private void UpdateNightMode(bool enabled)
    {
        if (_nightFilter is null)
        {
            if (!enabled)
                return;
            _nightFilter = new CanvasLayer { Layer = 120 };
            AddChild(_nightFilter);
            _nightFilter.AddChild(new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
            var filter = new ColorRect
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/night_mode.gdshader") }
            };
            _nightFilter.AddChild(filter);
            filter.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _nightFilter.Visible = enabled;
    }
}
