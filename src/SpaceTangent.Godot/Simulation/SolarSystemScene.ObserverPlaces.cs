// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private ObserverPlaceAutosave? _observerPlaces;
    private bool _restoredStartupWorkspace;

    private void RestoreObserverPlaces()
    {
        if (_startupArguments.Any(argument => argument is
            "--smoke-startup" or "--startup-scenario" or "--startup-default-settings" or "--startup-replay")) return;
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVER_PLACES_PATH");
        // Freeze the path before probes or UI actions temporarily change the Delta-T file.
        _observerPlaces = new(Session!, string.IsNullOrWhiteSpace(configured)
            ? SettingsPath() + ".observer-places.json" : configured);
        try
        {
            int count = _observerPlaces.Restore(adoptSingleDefault: !_restoredStartupWorkspace);
            if (count > 0)
            {
                Refresh();
                GD.Print($"SpaceTangent restored observing places for {count} views.");
            }
        }
        catch (Exception error) when (IsLoadError(error))
        { _status.Text = $"Could not restore observing places. {error.Message}"; }
    }

    private void AdvanceObserverPlaces(double delta)
    {
        try { _observerPlaces?.Advance(delta); }
        catch (Exception error) when (IsLoadError(error))
        { _status.Text = $"Could not save observing places. {error.Message}"; }
    }

    private void FlushObserverPlaces()
    {
        try { _observerPlaces?.Flush(); }
        catch (Exception error) when (IsLoadError(error))
        { _status.Text = $"Could not save observing places. {error.Message}"; }
    }
}
