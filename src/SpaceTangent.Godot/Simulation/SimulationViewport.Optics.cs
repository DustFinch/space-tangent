// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private CanvasLayer? _opticalLayer;
    private ShaderMaterial? _opticalMaterial;
    private VisualOptics? _opticalView;
    private bool _opticalGround;
    private double _opticalWanderPixels;
    private (ObserverView Observer, WorldSnapshot World, DeltaTModel DeltaT)? _lastOpticalFrame;
    private Vector2 _lastOpticalSize;
    private ulong _nextOpticalUpdate;

    internal bool EyepieceVisible => _opticalLayer?.Visible == true;

    public override void _Process(double delta)
    {
        if (_lastOpticalFrame is not { } frame)
            return;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (size != _lastOpticalSize)
        {
            _lastOpticalSize = size;
            Draw(frame.Observer, frame.World, frame.DeltaT);
        }
        ulong now = Time.GetTicksMsec();
        if (_opticalWanderPixels > 0 && now >= _nextOpticalUpdate)
        {
            _nextOpticalUpdate = now + 40;
            ApplyWander();
        }
    }

    private double OpticalField(ObserverView observer)
    {
        if (observer.ActiveOptics is not { } optics)
            return observer.EffectiveFieldOfView;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        double ratio = size.Y / Math.Max(1, Math.Min(size.X, size.Y));
        return Math.Atan(Math.Tan(optics.TrueFieldDegrees() * Math.PI / 360) * ratio) * 360 / Math.PI;
    }

    private void DrawEyepiece(ObserverView observer)
    {
        var profile = observer.ActiveOptics;
        if (profile is null)
        {
            _opticalLayer?.Hide();
            _opticalView = null;
            return;
        }
        if (_opticalLayer is null)
        {
            _opticalLayer = new CanvasLayer { Layer = 24 };
            AddChild(_opticalLayer);
            _opticalLayer.AddChild(new BackBufferCopy { CopyMode = BackBufferCopy.CopyModeEnum.Viewport });
            _opticalMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/eyepiece.gdshader") };
            var mask = new ColorRect { Material = _opticalMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
            _opticalLayer.AddChild(mask);
            mask.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        _opticalView = profile;
        _opticalGround = observer.Mode == CameraMode.Surface && observer.Navigation is null;
        _opticalLayer.Show();
        UpdateEyepiece();
    }

    private void UpdateEyepiece()
    {
        if (_opticalView is not { } optics || _opticalMaterial is not { } material)
            return;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        double pixelsPerArcsecond = Math.Min(size.X, size.Y) / (3600 * optics.TrueFieldDegrees());
        double blur = Math.Min(16, pixelsPerArcsecond * optics.BlurArcseconds(_opticalGround));
        _opticalWanderPixels = Math.Min(12, pixelsPerArcsecond * optics.WanderArcseconds(_opticalGround));
        material.SetShaderParameter("blur_pixels", (float)blur);
        ApplyWander();
    }

    private void ApplyWander()
    {
        if (_opticalMaterial is not { } material || _opticalView is null)
            return;
        double time = Time.GetTicksMsec() / 1000.0;
        material.SetShaderParameter("wander_pixels", new Vector2((float)(Math.Sin(time * 19) + .32 * Math.Sin(time * 37)),
            (float)(Math.Cos(time * 23) + .27 * Math.Sin(time * 51))) * (float)_opticalWanderPixels);
    }

    private bool InsideEyepiece(Vector2 position)
    {
        if (_opticalView is null)
            return true;
        Vector2 size = GetViewport().GetVisibleRect().Size;
        float radius = Math.Min(size.X, size.Y) / 2;
        return position.DistanceSquaredTo(size / 2) <= radius * radius;
    }
}
