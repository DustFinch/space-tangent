// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private ObserverOpticsLibrary? _opticsLibrary;

    private void ChangeOptics(bool cycleTube, bool cycleEyepiece, bool flip, bool barlow)
    {
        var view = Session![_active];
        if (!view.SupportsOptics)
        {
            _status.Text = "A virtual eyepiece needs a perspective flight or Earth surface view.";
            return;
        }
        var optics = view.Optics ?? new(ObserverOpticsLibrary.Default, false);
        VisualOptics profile = optics.Profile;
        if (cycleTube || cycleEyepiece)
        {
            try
            {
                string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_OPTICS_FILE");
                _opticsLibrary ??= ObserverOpticsLibrary.Load(string.IsNullOrWhiteSpace(configured)
                    ? ProjectSettings.GlobalizePath("user://optics-v1.json") : configured);
                profile = cycleTube ? profile with { Tube = _opticsLibrary.Next(profile.Tube) }
                    : profile with { Eyepiece = _opticsLibrary.Next(profile.Eyepiece) };
            }
            catch (Exception error) when (IsLoadError(error))
            {
                _status.Text = "Could not read local optics profiles. " + error.Message;
                return;
            }
        }
        else if (flip)
            profile = profile with { Inverted = !profile.Inverted };
        else if (barlow)
            profile = profile with { Barlow = profile.Barlow < 3 ? profile.Barlow + 1 : 1 };
        else
            optics = optics with { Active = !optics.Active };
        if (!profile.IsValid())
        {
            _status.Text = "This optical combination is outside the supported field.";
            return;
        }
        Session.SetOptics(_active, optics with { Profile = profile });
        Refresh();
        _status.Text = FormattableString.Invariant(
            $"{profile.Tube.Name} · {profile.Eyepiece.Name} · {profile.Magnification():F0}× · field {profile.TrueFieldDegrees():F3}° · {(optics.Active ? "active" : "stored")}");
    }
}
