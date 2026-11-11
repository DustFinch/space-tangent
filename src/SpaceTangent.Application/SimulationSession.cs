// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public enum CameraMode { Solar, Lunar, Satellites, Surface, Mission }

public readonly record struct ObserverView(JulianInstant Epoch, CameraMode Mode, ObjectId? Selection, bool Playing);

public sealed class SimulationWorld
{
    public SimulationWorld(De440Ephemeris ephemeris, IEnumerable<ScienceSatellite> satellites,
        SpiceSpacecraft? mission = null)
    {
        Ephemeris = ephemeris;
        Satellites = Array.AsReadOnly(satellites.ToArray());
        var graph = new WorldGraphBuilder();
        graph.AddSolarSystem(ephemeris);
        foreach (ScienceSatellite satellite in Satellites)
            graph.AddScienceSatellite(satellite);
        if (mission is not null)
        {
            MissionId = graph.AddSpiceSpacecraft(mission);
            MissionName = mission.Name;
        }
        Graph = graph.Build();
    }

    public De440Ephemeris Ephemeris { get; }
    public IReadOnlyList<ScienceSatellite> Satellites { get; }
    public WorldGraph Graph { get; }
    public ObjectId? MissionId { get; }
    public string? MissionName { get; }
}

/// <summary>Owns clocks and view preferences independently of rendering resources.</summary>
public sealed class SimulationSession
{
    // A recorded Cassini Saturn orbit insertion epoch with both state and pointing coverage.
    public static JulianInstant MissionPreview { get; } = JulianInstant.FromTdbSeconds(141917558.4203589);
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

    public SimulationScenario CaptureScenario(int activeView) =>
        new(Array.AsReadOnly(_views.ToArray()), Synchronized, _clockOwner, activeView, DeltaT);

    public void RestoreScenario(SimulationScenario scenario)
    {
        ObserverView[] views = scenario.Views.ToArray();
        if (views.Length is < 1 or > 2 || scenario.ClockOwner < 0 || scenario.ClockOwner >= views.Length ||
            scenario.ActiveView < 0 || scenario.ActiveView >= views.Length)
            throw new InvalidDataException("The scenario refers to an unavailable view.");
        if (!Enum.IsDefined(scenario.DeltaT))
            throw new InvalidDataException("The scenario has an unknown ΔT model.");
        foreach (ObserverView view in views)
        {
            if (!Covers(World, view.Epoch))
                throw new InvalidDataException("The scenario epoch is outside the current ephemeris.");
            if (!Enum.IsDefined(view.Mode))
                throw new InvalidDataException("The scenario has an unknown camera mode.");
            if (view.Mode == CameraMode.Mission && World.MissionId is null)
                throw new InvalidDataException("The scenario needs the Cassini mission add-on.");
            if (view.Mode == CameraMode.Satellites && World.Satellites.Count == 0)
                throw new InvalidDataException("The scenario needs the Earth satellite add-ons.");
            if (view.Selection is { } selection && !World.Satellites.Any(satellite => satellite.Id == selection))
                throw new InvalidDataException($"The selected object {selection.Value} is missing from the current world.");
        }
        ObserverView owner = views[scenario.ClockOwner];
        if (scenario.Synchronized && views.Any(view => view.Epoch != owner.Epoch || view.Playing != owner.Playing))
            throw new InvalidDataException("Synchronized scenario clocks must agree.");

        _views.Clear();
        _views.AddRange(views);
        Synchronized = scenario.Synchronized;
        _clockOwner = scenario.ClockOwner;
        DeltaT = scenario.DeltaT;
    }

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
        if (mode is < CameraMode.Solar or > CameraMode.Mission)
            throw new ArgumentOutOfRangeException(nameof(mode));
        ObserverView previous = _views[view];
        if (mode == CameraMode.Satellites)
        {
            ScienceSatellite target = World.Satellites.FirstOrDefault(satellite => satellite.Id == previous.Selection)
                ?? World.Satellites.FirstOrDefault()
                ?? throw new InvalidOperationException("The current world has no satellites.");
            Select(view, target.Id);
        }
        if (mode == CameraMode.Mission)
        {
            ObjectId mission = World.MissionId ?? throw new InvalidOperationException("The current world has no mission.");
            WorldSnapshot snapshot = World.Graph.Evaluate(previous.Epoch);
            if (!snapshot.TryState(mission, out _) || !snapshot.TryOrientation(mission, out _))
                Seek(view, MissionPreview);
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

    public void TogglePlaying(int view) => SetPlaying(view, !_views[view].Playing);

    public void SetPlaying(int view, bool playing) => SetClock(view, _views[view].Epoch, playing);

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
                double rate = _views[view].Mode is CameraMode.Satellites or CameraMode.Mission ? 180 : JulianInstant.SecondsPerDay;
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
            bool lostMode = (previous.Mode == CameraMode.Satellites && next.Satellites.Count == 0) ||
                (previous.Mode == CameraMode.Mission && next.MissionId is null);
            CameraMode mode = lostMode
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
