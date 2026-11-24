// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

internal sealed record ObserverNavigationActions(Action<int> ActivateView, Action<ObjectId> SelectObject,
    Action<int, bool, double> Magnification, Action<ViewControls, SkyLayerSettings> PointerReadout,
    Action<string> Status, Action Refresh);

// Translates engine input into session operations and owns held movement keys
// and per-view inertia. Panel construction and presentation stay with their owners.
internal sealed class ObserverNavigationInput(Control owner, ObserverViewHost host)
{
    private readonly Control _owner = owner;
    private readonly ObserverViewHost _host = host;
    private SimulationSession _session = null!;
    private ObserverNavigationActions _actions = null!;
    private List<ViewControls> _views => _host.Views;
    private int _active => _host.Active;
    private readonly HashSet<Key> _freeFlightKeys = [];
    private bool _freeFlightBoost;

    internal void Initialize(SimulationSession session, ObserverNavigationActions actions)
    {
        _session = session;
        _actions = actions;
    }

    internal void ClearKeys() => _freeFlightKeys.Clear();

    internal void FocusLost()
    {
        _freeFlightKeys.Clear();
        _freeFlightBoost = false;
        foreach (var view in _views) ClearCameraInertia(view);
    }

    internal void HandleGlobalInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false })
            foreach (var view in _views)
                view.DraggingCamera = false;
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            foreach (var view in _views)
                if (view.LookPress.HasValue)
                    DeferLookRelease(view);

        if (input is InputEventKey flightKey)
        {
            _freeFlightBoost = FreeFlightKey(flightKey) == Key.Shift ? flightKey.Pressed : flightKey.ShiftPressed;
            if (!flightKey.Pressed)
                _freeFlightKeys.Remove(FreeFlightKey(flightKey));
            if (flightKey.CtrlPressed || flightKey.AltPressed || flightKey.MetaPressed)
                _freeFlightKeys.Clear();
        }
    }

    internal bool TryFreeFlightKey(InputEventKey key)
    {
        if (key.CtrlPressed || key.AltPressed || key.MetaPressed || _owner.GetViewport().GuiGetFocusOwner() is LineEdit)
            return false;
        Key movement = FreeFlightKey(key);
        if (movement is not (Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.R or Key.F) ||
            !_session.CanMoveFreeFlight(_active))
            return false;
        if (!key.Echo)
            _freeFlightKeys.Add(movement);
        _freeFlightBoost = key.ShiftPressed;
        return true;
    }

    private static Key FreeFlightKey(InputEventKey key) => key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode;

    internal bool StepFreeFlight(double delta)
    {
        if (_freeFlightKeys.Count == 0)
            return false;
        var session = _session;
        if (!session.CanMoveFreeFlight(_active) || _owner.GetViewport().GuiGetFocusOwner() is LineEdit)
        {
            _freeFlightKeys.Clear();
            return false;
        }
        int right = (Held(Key.D) ? 1 : 0) - (Held(Key.A) ? 1 : 0);
        int up = (Held(Key.E) || Held(Key.R) ? 1 : 0) - (Held(Key.Q) || Held(Key.F) ? 1 : 0);
        int forward = (Held(Key.W) ? 1 : 0) - (Held(Key.S) ? 1 : 0);
        return session.MoveFreeFlight(_active, right, up, forward, delta, _freeFlightBoost);

        bool Held(Key key) => _freeFlightKeys.Contains(key);
    }

    internal void HandleViewInput(int view, InputEvent input)
    {
        if (input is InputEventMouseButton { Pressed: true })
            _actions.ActivateView(view);
        var controls = _views[view];
        if (input is InputEventMouseMotion pointer)
        {
            controls.PointerPosition = pointer.Position;
            _actions.PointerReadout(controls, _session[view].Sky);
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right } right)
        {
            controls.DraggingCamera = right.Pressed;
            if (right.Pressed)
                controls.CameraDragVelocity = Vector2.Zero;
        }
        var measurements = controls.Measurements;
        if (measurements.HandleAngleInput(input))
        {
            if (measurements.AngleDegrees is { } angle)
                _actions.Status("Angle " + MeasurementOverlay.FormatAngle(angle));
            _owner.GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } left)
        {
            if (left.Pressed)
            {
                controls.LookGesture++;
                controls.LookPress = left.Position;
                controls.Looking = false;
                controls.CameraDragVelocity = Vector2.Zero;
            }
            else if (controls.LookPress is { } press)
            {
                controls.LookPress = null;
                if (!controls.Looking && left.Position.DistanceTo(press) <= 4 &&
                    controls.Renderer.PickObject(press) is { } target)
                    _actions.SelectObject(target);
                controls.Looking = false;
            }
            _owner.GetViewport().SetInputAsHandled();
            return;
        }
        bool adjusted = input switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel =>
                AdjustWheel(view, wheel),
            InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Right) != 0 =>
                ApplyCameraMotion(view, motion, lookAround: false),
            InputEventMouseMotion motion when controls.LookPress is { } press &&
                (motion.ButtonMask & MouseButtonMask.Left) != 0 &&
                (controls.Looking || motion.Position.DistanceTo(press) > 4) =>
                ApplyCameraMotion(view, motion, lookAround: true),
            _ => false
        };
        if (adjusted)
        {
            _owner.GetViewport().SetInputAsHandled();
            _actions.Refresh();
        }
    }

    private static void DeferLookRelease(ViewControls controls)
    {
        // Let the viewport resolve a click before cleaning up releases outside it.
        int gesture = controls.LookGesture;
        Callable.From(() =>
        {
            if (controls.LookGesture != gesture)
                return;
            controls.LookPress = null;
            controls.Looking = false;
        }).CallDeferred();
    }

    private const double CameraInertiaSeconds = .3;

    private bool ApplyCameraMotion(int view, InputEventMouseMotion motion, bool lookAround)
    {
        var controls = _views[view];
        if (lookAround)
            controls.Looking = true;
        bool moved = AdjustDrag(view, motion.Relative, lookAround);
        if (moved && (controls.DraggingCamera || controls.Looking) && _session[view].CameraInertia &&
            !_session.InShip(view) && motion.Velocity.IsFinite())
        {
            controls.CameraCoastLooksAround = lookAround;
            controls.CameraDragVelocity = controls.CameraDragVelocity.Lerp(motion.Velocity.LimitLength(4000), .5f);
        }
        return moved;
    }

    internal bool StepCameraInertia(double elapsed)
    {
        if (!(elapsed > 0))
            return false;
        double decay = Math.Exp(-Math.Min(elapsed, .1) / CameraInertiaSeconds);
        double integrated = CameraInertiaSeconds * (1 - decay);
        bool changed = false;
        var session = _session;
        for (int index = 0; index < _views.Count; index++)
        {
            var controls = _views[index];
            if (!session[index].CameraInertia || session.IsFlying(index) || session.InShip(index))
            {
                controls.CameraDragVelocity = Vector2.Zero;
                controls.CameraDollyRate = 0;
                continue;
            }
            if (!controls.DraggingCamera && !controls.Looking && controls.CameraDragVelocity.LengthSquared() > 1)
            {
                changed |= AdjustDrag(index, controls.CameraDragVelocity * (float)integrated, controls.CameraCoastLooksAround);
                controls.CameraDragVelocity *= (float)decay;
            }
            if (Math.Abs(controls.CameraDollyRate) > .001)
            {
                changed |= session.AdjustCamera(index, 0, 0, Math.Exp(controls.CameraDollyRate * integrated));
                controls.CameraDollyRate *= decay;
            }
        }
        return changed;
    }

    internal bool StopCameraMotion(int index, out bool stoppedFlight)
    {
        var controls = _views[index];
        stoppedFlight = _session.CancelFlight(index);
        bool moving = _freeFlightKeys.Count > 0 || controls.DraggingCamera || controls.Looking ||
            controls.CameraDragVelocity.LengthSquared() > 1 ||
            Math.Abs(controls.CameraDollyRate) > .001;
        ClearCameraInertia(controls);
        _freeFlightKeys.Clear();
        return stoppedFlight || moving;
    }

    internal static void ClearCameraInertia(ViewControls controls)
    {
        controls.DraggingCamera = false;
        controls.LookPress = null;
        controls.Looking = false;
        controls.CameraDragVelocity = Vector2.Zero;
        controls.CameraDollyRate = 0;
    }

    private bool AdjustDrag(int view, Vector2 pixels, bool lookAround)
    {
        var session = _session;
        if (!pixels.IsFinite())
            return false;
        var observer = session[view];
        double sensitivity = observer.MouseSensitivity ?? 1;
        if (session.InShip(view))
            return session.AdjustCamera(view, -.006 * pixels.X * sensitivity, .006 * pixels.Y * sensitivity, 1);
        bool ground = observer is { Mode: CameraMode.Surface, Navigation: null };
        bool look = lookAround || ground || observer.Navigation is { Target: null };
        const double radiansPerPixel = .003;
        if (look)
            sensitivity *= Math.Tan(observer.EffectiveFieldOfView * Math.PI / 360) / Math.Tan(50 * Math.PI / 360);
        double yaw = Math.Clamp(-pixels.X * radiansPerPixel * sensitivity, -1, 1);
        double pitch = Math.Clamp(-pixels.Y * radiansPerPixel * sensitivity, -1, 1);
        if (!ground)
            return session.AdjustCamera(view, yaw, pitch, 1, orbitTarget: !lookAround);

        var camera = observer.ObserverCamera;
        double direction = observer.ActiveOptics?.Inverted == true ? -1 : 1;
        session.SetEarthObserver(view, camera.Turn(direction * yaw, direction * pitch));
        return true;
    }

    private bool AdjustWheel(int view, InputEventMouseButton wheel)
    {
        var session = _session;
        var observer = session[view];
        bool inward = wheel.ButtonIndex == MouseButton.WheelUp;
        if (!session.InShip(view) && observer.ActiveOptics is not null ||
            wheel.CtrlPressed || observer is { Mode: CameraMode.Surface, Navigation: null })
        {
            _actions.Magnification(view, inward, .85);
            return true;
        }

        if (observer.CameraInertia && observer.Navigation is not null && !session.InShip(view))
        {
            session.CancelFlight(view);
            var controls = _views[view];
            controls.CameraDollyRate = Math.Clamp(controls.CameraDollyRate +
                (inward ? 1 : -1) * Math.Log(.85) / CameraInertiaSeconds, -20, 20);
            return true;
        }
        return session.AdjustCamera(view, 0, 0, inward ? .8 : 1.25);
    }

}
