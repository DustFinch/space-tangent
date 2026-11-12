// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private ColorRect? _landscape;
    private ShaderMaterial? _landscapeMaterial;
    private LandscapeTextures? _landscapeTextures;

    internal string? VisibleLandscape => _surface.Visible && _landscape?.Visible == true ? _landscapeTextures?.Name : null;

    internal void SetLandscape(LandscapeTextures? textures)
    {
        if (ReferenceEquals(textures, _landscapeTextures))
            return;
        _landscapeTextures = textures;
        if (textures is null)
        {
            _landscape?.Hide();
            _landscapeMaterial?.SetShaderParameter("landscape_ground", default(Variant));
            _landscapeMaterial?.SetShaderParameter("landscape_lights", default(Variant));
            _landscapeMaterial?.SetShaderParameter("landscape_fog", default(Variant));
            return;
        }
        if (_landscape is null)
        {
            _landscapeMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_landscape.gdshader") };
            _landscape = new ColorRect { Material = _landscapeMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
            _surface.AddChild(_landscape);
            _surface.MoveChild(_landscape, _meteors is null ? 0 : 2);
            _landscape.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        SetLayer("ground", textures.Ground);
        SetLayer("lights", textures.Lights);
        SetLayer("fog", textures.Fog);
        _landscapeMaterial!.SetShaderParameter("has_lights", textures.Lights is not null);
        _landscapeMaterial.SetShaderParameter("has_fog", textures.Fog is not null);
        _landscape.Show();

        void SetLayer(string layer, LandscapeTexture? source)
        {
            _landscapeMaterial!.SetShaderParameter($"landscape_{layer}", source is null ? default(Variant) : source.Texture);
            if (source is { Bounds: var bounds })
                _landscapeMaterial.SetShaderParameter($"{layer}_bounds", new Vector3((float)bounds.Top, (float)bounds.Bottom,
                    (float)bounds.Rotation));
        }
    }

    private void DrawLandscape(SurfaceLighting lighting, Vector3 forward, Vector3 right, Vector3 cameraUp,
        Vector3 north, Vector3 east, Vector3 up)
    {
        if (_landscape?.Visible != true || _landscapeMaterial is not { } material)
            return;
        float altitude = (float)(lighting.SolarAltitudeRadians * 180 / Math.PI);
        float daylight = Mathf.SmoothStep(-12, 8, altitude);
        float moonlight = (float)Math.Clamp(.035 * lighting.RelativeLunarFlux, 0, .05);
        material.SetShaderParameter("day_factor", .045f + daylight * .92f + (1 - daylight) * moonlight);
        material.SetShaderParameter("night_factor", 1 - Mathf.SmoothStep(-10, 1, altitude));
        material.SetShaderParameter("forward", forward);
        material.SetShaderParameter("right", right);
        material.SetShaderParameter("camera_up", cameraUp);
        material.SetShaderParameter("north", north);
        material.SetShaderParameter("east", east);
        material.SetShaderParameter("zenith", up);
        material.SetShaderParameter("tan_half_field", _tanHalfField);
    }
}
