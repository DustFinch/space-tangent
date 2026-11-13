// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private ObserverOpticsLibrary? _opticsLibrary;

    private ObserverOpticsLibrary ReadOpticsLibrary()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_OPTICS_FILE");
        return _opticsLibrary ??= ObserverOpticsLibrary.Load(string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://optics-v1.json") : configured);
    }

    private void ChangeViewMagnification(int index, bool narrower, double fieldStep)
    {
        var session = Session!;
        var view = session[index];
        if (view.ActiveOptics is not { } current)
        {
            double factor = narrower ? fieldStep : 1 / fieldStep;
            session.SetFieldOfView(index, Math.Clamp(view.EffectiveFieldOfView * factor, 2, 120));
            return;
        }

        try
        {
            var ordered = ReadOpticsLibrary().Eyepieces.Append(current.Eyepiece).Distinct()
                .OrderByDescending(eyepiece => eyepiece.FocalMillimeters).ToList();
            int next = ordered.IndexOf(current.Eyepiece) + (narrower ? 1 : -1);
            if (next < 0 || next >= ordered.Count)
                return;
            var profile = current with { Eyepiece = ordered[next] };
            if (profile.IsValid())
                session.SetOptics(index, view.Optics! with { Profile = profile });
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = "Could not read local optics profiles. " + error.Message;
        }
    }

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
                var library = ReadOpticsLibrary();
                profile = cycleTube ? profile with { Tube = library.Next(profile.Tube) }
                    : profile with { Eyepiece = library.Next(profile.Eyepiece) };
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
