// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public enum CameraMode { Solar, Lunar, Satellites, Surface }

public readonly record struct ObserverView(JulianInstant Epoch, CameraMode Mode, ObjectId? Selection, bool Playing);

public sealed class SimulationWorld
{
    public SimulationWorld(De440Ephemeris ephemeris, IEnumerable<ScienceSatellite> satellites)
    {
        Ephemeris = ephemeris;
        Satellites = Array.AsReadOnly(satellites.ToArray());
    }

    public De440Ephemeris Ephemeris { get; }
    public IReadOnlyList<ScienceSatellite> Satellites { get; }
}

/// <summary>Owns clocks and view preferences independently of rendering resources.</summary>
public sealed class SimulationSession
{
    private readonly List<ObserverView> _views;
    private int _clockOwner;

    public SimulationSession(SimulationWorld world, JulianInstant initialEpoch, DeltaTModel deltaT)
    {
        if (!Covers(world, initialEpoch))
            throw new ArgumentOutOfRangeException(nameof(initialEpoch));
        World = world;
        _views = [new(initialEpoch, CameraMode.Solar, null, false)];
        SetDeltaT(deltaT);
    }

    public SimulationWorld World { get; private set; }
    public DeltaTModel DeltaT { get; private set; }
    public bool Synchronized { get; private set; } = true;
    public int ViewCount => _views.Count;
    public ObserverView this[int view] => _views[view];

    public void Split()
    {
        if (_views.Count == 1)
            _views.Add(_views[0]);
    }

    public void CloseSecondView()
    {
        if (_views.Count == 2)
            _views.RemoveAt(1);
        _clockOwner = 0;
    }

    public void Synchronize(bool enabled, int source)
    {
        ObserverView anchor = _views[source];
        if (Synchronized == enabled)
            return;
        Synchronized = enabled;
        if (enabled)
            SetClock(source, anchor.Epoch, anchor.Playing);
    }

    public void SetDeltaT(DeltaTModel model)
    {
        if (model is < DeltaTModel.EspenakMeeus2006 or > DeltaTModel.FixedOffset)
            throw new ArgumentOutOfRangeException(nameof(model));
        DeltaT = model;
    }

    public void SetMode(int view, CameraMode mode)
    {
        if (mode is < CameraMode.Solar or > CameraMode.Surface)
            throw new ArgumentOutOfRangeException(nameof(mode));
        ObserverView previous = _views[view];
        if (mode == CameraMode.Satellites)
        {
            ScienceSatellite target = World.Satellites.FirstOrDefault(satellite => satellite.Id == previous.Selection)
                ?? World.Satellites.FirstOrDefault()
                ?? throw new InvalidOperationException("The current world has no satellites.");
            Select(view, target.Id);
        }
        _views[view] = _views[view] with { Mode = mode };
    }

    public void Select(int view, ObjectId selection)
    {
        ObserverView previous = _views[view];
        ScienceSatellite target = World.Satellites.FirstOrDefault(satellite => satellite.Id == selection)
            ?? throw new ArgumentOutOfRangeException(nameof(selection));
        bool needsEpoch = !target.TrySample(previous.Epoch, out _);
        if (needsEpoch && !Covers(World, target.Epoch))
            throw new InvalidOperationException("The selected orbit and the current ephemeris have no common starting epoch.");

        _views[view] = previous with { Selection = selection };
        if (needsEpoch)
            SetClock(view, target.Epoch, previous.Playing);
    }

    public void TogglePlaying(int view) => SetClock(view, _views[view].Epoch, !_views[view].Playing);

    public void Seek(int view, JulianInstant epoch)
    {
        if (!Covers(World, epoch))
            throw new ArgumentOutOfRangeException(nameof(epoch));
        SetClock(view, epoch, _views[view].Playing);
    }

    public void Step(int view, double seconds)
    {
        if (!double.IsFinite(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds));
        ObserverView previous = _views[view];
        double first = (double)World.Ephemeris.Start.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        double last = (double)World.Ephemeris.End.DifferenceNanoseconds(previous.Epoch) / JulianInstant.NanosecondsPerSecond;
        if (seconds <= first)
            SetClock(view, World.Ephemeris.Start, false);
        else if (seconds >= last)
            SetClock(view, World.Ephemeris.End, false);
        else
            SetClock(view, previous.Epoch.AddSeconds(seconds), previous.Playing);
    }

    public bool Tick(double elapsed)
    {
        bool changed = false;
        int start = Synchronized ? _clockOwner : 0;
        int end = Synchronized ? start + 1 : _views.Count;
        for (int view = start; view < end; view++)
        {
            if (_views[view].Playing)
            {
                double rate = _views[view].Mode == CameraMode.Satellites ? 180 : JulianInstant.SecondsPerDay;
                Step(view, elapsed * rate);
                changed = true;
            }
        }
        return changed;
    }

    public bool TryReplaceWorld(SimulationWorld next)
    {
        if (_views.Any(view => !Covers(next, view.Epoch)))
            return false;

        for (int index = 0; index < _views.Count; index++)
        {
            ObserverView previous = _views[index];
            ObjectId? selected = previous.Selection is { } id && next.Satellites.Any(satellite => satellite.Id == id)
                ? id
                : null;
            CameraMode mode = previous.Mode == CameraMode.Satellites && next.Satellites.Count == 0
                ? CameraMode.Solar
                : previous.Mode;
            _views[index] = previous with { Selection = selected, Mode = mode };
        }
        World = next;
        return true;
    }

    private void SetClock(int view, JulianInstant epoch, bool playing)
    {
        if (!Synchronized)
        {
            _views[view] = _views[view] with { Epoch = epoch, Playing = playing };
            return;
        }
        _clockOwner = view;
        for (int index = 0; index < _views.Count; index++)
            _views[index] = _views[index] with { Epoch = epoch, Playing = playing };
    }

    private static bool Covers(SimulationWorld world, JulianInstant epoch) =>
        epoch.Scale == TimeScale.Tdb && epoch.CompareTo(world.Ephemeris.Start) >= 0 &&
        epoch.CompareTo(world.Ephemeris.End) <= 0;
}
