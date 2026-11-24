// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

/// <summary>Owns the frozen preferences path, revision tracking and debounced writes for one live session.</summary>
public sealed class ObserverPlaceAutosave
{
    private readonly SimulationSession _session;
    private readonly string _path;
    private int _revision;
    private double _delay;
    private bool _dirty;

    public ObserverPlaceAutosave(SimulationSession session, string path)
    {
        _session = session;
        _path = Path.GetFullPath(path);
    }

    public int Restore(bool adoptSingleDefault)
    {
        try
        {
            return File.Exists(_path)
                ? ObserverPlacePreferences.Apply(ObserverPlacePreferences.Load(_path), _session, adoptSingleDefault)
                : 0;
        }
        finally { _revision = _session.ObserverPlacesVersion; }
    }

    public void Advance(double delta)
    {
        if (_revision != _session.ObserverPlacesVersion)
        {
            _revision = _session.ObserverPlacesVersion;
            _dirty = true;
            _delay = .5;
        }
        if (_dirty && (_delay -= delta) <= 0) Flush();
    }

    public void Flush()
    {
        if (!_dirty && _revision == _session.ObserverPlacesVersion) return;
        try
        {
            ObserverPlacePreferences.Save(_path, _session);
            _dirty = false;
            _revision = _session.ObserverPlacesVersion;
        }
        catch
        {
            _delay = 5;
            throw;
        }
    }
}
