// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private bool TryClockKey(InputEventKey key)
    {
        var session = Session!;
        switch (key.Keycode)
        {
            case Key.J:
                session.ReversePlayback(_active);
                break;
            case Key.K:
                session.AdjustPlaybackRate(_active, faster: false);
                break;
            case Key.L:
                session.AdjustPlaybackRate(_active, faster: true);
                break;
            case Key.Minus when !key.ShiftPressed:
                bool enable = !session[_active].LightTimeDelayed;
                _status.Text = session.TrySetLightTimeDelay(_active, enable, out double seconds)
                    ? FormattableString.Invariant($"Light time {seconds:G6} s · clock moved {(enable ? "back" : "forward")}.")
                    : "Select a body with a light time inside the ephemeris.";
                Refresh();
                return true;
            default:
                if (key.Unicode != '?' && (key.Keycode != Key.Slash || !key.ShiftPressed))
                    return false;
                _status.Text = session.LightTimeSeconds(_active) is { } delay
                    ? FormattableString.Invariant($"Light travel time {delay:G6} s.")
                    : "Select a body to measure its light travel time.";
                return true;
        }
        _status.Text = FormattableString.Invariant($"Playback {session.CurrentPlaybackRate(_active):G6}×.");
        Refresh();
        return true;
    }
}
