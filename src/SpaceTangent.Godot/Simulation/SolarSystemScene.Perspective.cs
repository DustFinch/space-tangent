// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private readonly record struct ProjectionScale(double ScreenAngle, double Field, double Zoom, CameraMode Mode);
    private ulong _projectionBaselineUntil;
    private int _projectionScreen = -2, _projectionDpi = PerspectiveViewScale.DefaultDpi;

    private void ResetPerspectiveScale() => _projectionBaselineUntil = Engine.GetProcessFrames() + 2;

    private bool UpdatePerspectiveScale()
    {
        if (Session is not { } session)
            return false;
        int screen = GetWindow().CurrentScreen;
        if (_projectionScreen != screen)
        {
            _projectionScreen = screen;
            _projectionDpi = screen < 0 ? PerspectiveViewScale.DefaultDpi : DisplayServer.ScreenGetDpi(screen);
        }
        bool reset = Engine.GetProcessFrames() <= _projectionBaselineUntil;
        bool changed = false;
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls view = _views[index];
            ObserverView observer = session[index];
            double height = view.Canvas.Size.Y;
            if (height < 2 || observer.ActiveOptics is not null)
                continue;
            double field = observer.EffectiveFieldOfView;
            double angle = PerspectiveViewScale.ScreenAngleDegrees(height, _projectionDpi);
            ProjectionScale? previous = view.Perspective;
            if (reset || previous is not { } prior || prior.Mode != observer.Mode || prior.Field != field)
            {
                view.Perspective = new(angle, field, angle / field, observer.Mode);
                continue;
            }
            if (prior.ScreenAngle == angle)
                continue;

            double adjusted = Math.Clamp(angle / prior.Zoom, 2, 120);
            view.Perspective = prior with { ScreenAngle = angle, Field = adjusted };
            if (adjusted != field)
            {
                session.SetFieldOfView(index, adjusted);
                changed = true;
            }
        }
        return changed;
    }
}
