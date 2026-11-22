// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private string? _observerPlacesPath;
    private int _observerPlacesRevision;
    private double _observerPlacesDelay;
    private bool _observerPlacesDirty;
    private bool _observerPlacesEnabled;
    private bool _restoredStartupWorkspace;

    private void RestoreObserverPlaces()
    {
        _observerPlacesEnabled = !_startupArguments.Any(argument => argument is
            "--smoke-startup" or "--startup-scenario" or "--startup-default-settings" or "--startup-replay");
        if (!_observerPlacesEnabled) return;
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVER_PLACES_PATH");
        // Freeze the path before probes or UI actions temporarily change the Delta-T file.
        _observerPlacesPath = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? SettingsPath() + ".observer-places.json" : configured);
        {
            try
            {
                if (File.Exists(_observerPlacesPath))
                {
                    int count = ObserverPlacePreferences.Apply(ObserverPlacePreferences.Load(_observerPlacesPath),
                        Session!, adoptSingleDefault: !_restoredStartupWorkspace);
                    if (count > 0)
                    {
                        Refresh();
                        GD.Print($"SpaceTangent restored observing places for {count} views.");
                    }
                }
            }
            catch (Exception error) when (IsLoadError(error))
            { _status.Text = $"Could not restore observing places. {error.Message}"; }
        }
        _observerPlacesRevision = Session!.ObserverPlacesVersion;
    }

    private void AdvanceObserverPlaces(double delta)
    {
        if (!_observerPlacesEnabled || Session is null || _observerPlacesPath is null) return;
        if (_observerPlacesRevision != Session.ObserverPlacesVersion)
        {
            _observerPlacesRevision = Session.ObserverPlacesVersion;
            _observerPlacesDirty = true;
            _observerPlacesDelay = .5;
        }
        if (_observerPlacesDirty && (_observerPlacesDelay -= delta) <= 0)
            FlushObserverPlaces();
    }

    private void FlushObserverPlaces()
    {
        if (!_observerPlacesEnabled || Session is null || _observerPlacesPath is null ||
            (!_observerPlacesDirty && _observerPlacesRevision == Session.ObserverPlacesVersion)) return;
        try
        {
            ObserverPlacePreferences.Save(_observerPlacesPath, Session);
            _observerPlacesDirty = false;
            _observerPlacesRevision = Session.ObserverPlacesVersion;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not save observing places. {error.Message}";
            _observerPlacesDelay = 5;
        }
    }
}
