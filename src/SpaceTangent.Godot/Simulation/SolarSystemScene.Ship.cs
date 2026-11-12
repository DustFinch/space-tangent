// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private readonly HashSet<Key> _shipKeys = [];
    private bool _shipBoost;

    private bool HandleShipInput(InputEventKey key)
    {
        if (Session is not { } session)
            return false;
        if (key is { Pressed: true, Echo: false, CtrlPressed: true, ShiftPressed: true, Keycode: Key.B })
        {
            if (session.InShip(_active))
                ExitShip();
            else if (SimulationViewport.ShipModelAvailable)
            {
                session.BoardShip(_active);
                _status.Text = "Ship · W/S throttle, A/D/R/F steer, Q/E roll, X stop, C cockpit. Ctrl+Shift+G approach, Ctrl+Shift+O orbit.";
                Refresh();
            }
            else
                _status.Text = "The ship model is unavailable.";
            return true;
        }
        if (!session.InShip(_active))
            return false;

        if (!key.Pressed && _shipKeys.Remove(key.Keycode))
            return true;
        if (key is { Pressed: true, Echo: false, CtrlPressed: true, ShiftPressed: true } &&
            key.Keycode is Key.G or Key.O or Key.M)
        {
            SetShipPilot(key.Keycode switch { Key.G => ShipPilot.Approach, Key.O => ShipPilot.Orbit, _ => ShipPilot.Manual });
            return true;
        }
        if (key.CtrlPressed || key.AltPressed || key.MetaPressed || GetViewport().GuiGetFocusOwner() is LineEdit)
            return false;
        _shipBoost = key.ShiftPressed;
        if (key.Pressed && key.Keycode is Key.W or Key.S or Key.A or Key.D or Key.R or Key.F or Key.Q or Key.E)
        {
            _shipKeys.Add(key.Keycode);
            return true;
        }
        if (key.Pressed && !key.Echo && key.Keycode is Key.X or Key.C)
        {
            if (key.Keycode == Key.X)
                session.SetShipThrottle(_active, 0);
            else
                session.ToggleShipCockpit(_active);
            Refresh();
            return true;
        }
        return false;
    }

    private void SetShipPilot(ShipPilot mode)
    {
        _status.Text = Session!.SetShipPilot(_active, mode)
            ? $"Ship autopilot · {mode}." : "Select an object with an available position.";
        Refresh();
    }

    private bool ExitShip()
    {
        if (!Session!.LeaveShip(_active))
            return false;
        _shipKeys.Clear();
        _status.Text = "Left the ship.";
        Refresh();
        return true;
    }

    private void FlyShip(double delta)
    {
        if (_shipKeys.Count == 0 || Session?.InShip(_active) != true)
            return;
        double dt = Math.Clamp(delta, 0, .25);
        int Held(Key positive, Key negative) => (_shipKeys.Contains(positive) ? 1 : 0) - (_shipKeys.Contains(negative) ? 1 : 0);
        Session.FlyShipManually(_active, Held(Key.W, Key.S) * dt * (_shipBoost ? 3 : 1),
            Held(Key.A, Key.D) * dt * .8, Held(Key.R, Key.F) * dt * .8, Held(Key.Q, Key.E) * dt * .8);
    }
}
