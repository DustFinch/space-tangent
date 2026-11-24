// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>Owns clocks and view preferences independently of rendering resources.</summary>
public sealed partial class SimulationSession
{
    // A recorded Cassini Saturn orbit insertion epoch with both state and pointing coverage.
    public static JulianInstant MissionPreview => CameraModeRules.MissionPreview;
    private readonly List<ObserverView> _views;
    /// <summary>View indices whose saved non-Earth site was reset by the last successful world replacement.</summary>
    public IReadOnlyList<int> SurfaceObserverFallbacks { get; private set; } = [];
    private readonly ObserverClock _clocks;
    private readonly List<int> _viewIds = [0];
    private int _nextViewId = 1;
    private readonly Dictionary<int, bool> _scriptedZoomChanges = [];
    public int ObserverPlacesVersion { get; private set; }

    public SimulationSession(SimulationWorld world, JulianInstant initialEpoch, DeltaTModel deltaT)
    {
        if (!Covers(world, initialEpoch))
            throw new ArgumentOutOfRangeException(nameof(initialEpoch));
        World = world;
        _views = [new(initialEpoch, CameraMode.Solar, null, false) { Places = default(ObserverPlaceMemory).EnsureIdentity() }];
        _clocks = new(_views);
        SetDeltaT(deltaT);
    }

    public SimulationWorld World { get; private set; }
    public DeltaTModel DeltaT { get; private set; }
    public bool Synchronized => _clocks.Synchronized;
    public bool NightMode { get; set; }
    public ViewSplit SplitLayout { get; private set; }
    public ObserverLayout Layout { get; private set; } = ObserverLayout.Leaf(0);
    public int ViewCount => _views.Count;
    public ObserverView this[int view] => _views[view];

    public int ViewIdentity(int view) => _viewIds[view];
    internal int IndexOfView(int identity) => _viewIds.IndexOf(identity);

    public SimulationScenario CaptureScenario(int activeView)
    {
        return new(Array.AsReadOnly(_views.ToArray()), Synchronized, _clocks.Owner, activeView, DeltaT)
            { SplitLayout = SplitLayout, Layout = Layout.IsDefault ? null : Layout, NightMode = NightMode };
    }

    public void RestoreScenario(SimulationScenario scenario)
    {
        var validated = ScenarioRestoration.Validate(World, scenario);
        ObserverView[] views = validated.Views;
        ObserverLayout layout = validated.Layout;
        ObserverView owner = validated.Owner;
        _ships.Clear();
        _views.Clear();
        _views.AddRange(views.Select(view => view with { Places = view.Places.EnsureIdentity() }));
        _flights.Clear();
        _trackingEpochs.Clear();
        _scriptNotices.Clear();
        _scriptMarkers.Clear();
        _viewIds.Clear();
        _scriptedZoomChanges.Clear();
        foreach (ObserverView view in views)
        {
            _flights.Add(null);
            _trackingEpochs.Add(view.Navigation is { Tracking: true, Target: { } target } ? (target, view.Epoch) : null);
            _viewIds.Add(_nextViewId++);
        }
        Layout = layout;
        _clocks.Restore(scenario.Synchronized, scenario.ClockOwner);
        NightMode = scenario.NightMode;
        if (Synchronized)
            SharePlaybackRate(owner.PlaybackRate ?? DefaultPlaybackRate(owner.Mode));
        SplitLayout = scenario.SplitLayout;
        DeltaT = scenario.DeltaT;
        ObserverPlacesVersion++;
    }

    public void SetEarthObserver(int view, EarthObserverCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view].WithObserverCamera(camera);
        ObserverPlacesVersion++;
    }

    public bool SetObserverBody(int view, ObjectId body)
    {
        var observer = _views[view];
        var camera = observer.CameraForBody(body);
        if (!SurfaceObserver.TryFrame(World, camera, World.Graph.Evaluate(observer.Epoch, DeltaT), DeltaT, out _)) return false;
        SetEarthObserver(view, camera);
        return true;
    }

    internal void RestoreObserverPlaces(int view, EarthObserverCamera camera, ObserverPlaceMemory memory)
    {
        if (!camera.IsValid() || !memory.HasIdentity || !memory.IsValid())
            throw new InvalidDataException("Invalid saved observing places.");
        var snapshot = World.Graph.Evaluate(_views[view].Epoch, DeltaT);
        if (!SurfaceObserver.TryFrame(World, camera, snapshot, DeltaT, out _))
        {
            memory = memory.Remember(camera);
            camera = memory.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
        }
        _views[view] = _views[view] with { EarthObserver = camera, Places = memory };
        ObserverPlacesVersion++;
    }

    public void SetOptics(int view, ObserverOptics optics)
    {
        if (!optics.IsValid())
            throw new ArgumentOutOfRangeException(nameof(optics));
        _views[view] = _views[view] with { Optics = optics };
    }

    public void SetSkyLayers(int view, SkyLayerSettings sky) => _views[view] = _views[view] with { Sky = sky };

    public void SetSplitLayout(ViewSplit layout)
    {
        if (!Enum.IsDefined(layout))
            throw new ArgumentOutOfRangeException(nameof(layout));
        SplitLayout = layout;
        if (Layout.View is null)
            Layout = Layout with { Direction = layout };
    }

    public void SetFieldOfView(int view, double? field)
    {
        if (field is { } degrees && !ObserverView.ValidFieldOfView(degrees))
            throw new ArgumentOutOfRangeException(nameof(field));
        _views[view] = _views[view] with { FieldOfViewDegrees = field };
    }

    internal void SetScriptFieldOfView(int view, double degrees, bool synchronizeZoom, bool synchronizeOtherViews)
    {
        SetFieldOfView(view, degrees);
        if (synchronizeZoom && synchronizeOtherViews)
        {
            foreach (int identity in _viewIds)
                _scriptedZoomChanges[identity] = true;
        }
        else if (synchronizeZoom)
            _scriptedZoomChanges[ViewIdentity(view)] = true;
        else
            _scriptedZoomChanges.TryAdd(ViewIdentity(view), false);
    }

    /// <summary>Consumes a script request to keep or reset the view's stored zoom. Null means no request.</summary>
    public bool? TakeScriptedZoomChange(int view) =>
        _scriptedZoomChanges.Remove(ViewIdentity(view), out bool synchronize) ? synchronize : null;

    public void Split()
    {
        if (_views.Count == 1)
            SplitView(0, SplitLayout);
    }

    public bool SplitView(int view, ViewSplit direction) => SplitView(view, direction, view, .5, false);

    public bool SplitView(int view, ViewSplit direction, int copyFrom, double share, bool newFirst)
    {
        if (!double.IsFinite(share) || share is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(share));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (_views.Count == ObserverLayout.MaximumViews)
            return false;
        if (Synchronized)
            SharePlaybackRate(CurrentPlaybackRate(copyFrom));
        ObserverView source = _views[copyFrom];
        Layout = Layout.Insert(view, _views.Count, direction, share, newFirst);
        SplitLayout = Layout.Direction;
        _views.Add(source with { Places = source.Places.ForkIdentity() });
        _flights.Add(null);
        _trackingEpochs.Add(_trackingEpochs[copyFrom]);
        _viewIds.Add(_nextViewId++);
        ObserverPlacesVersion++;
        return true;
    }

    public void ResizeViewDivider(string path, double share)
    {
        if (!double.IsFinite(share) || share is <= 0 or >= 1)
            return;
        Layout = Layout.Resize(path, share);
    }

    public void CloseSecondView()
    {
        if (_views.Count == 2)
            RemoveView(1);
    }

    public void RemoveView(int view)
    {
        if (_views.Count == 1)
            return;
        _views.RemoveAt(view);
        _flights.RemoveAt(view);
        _trackingEpochs.RemoveAt(view);
        _ships.Remove(ViewIdentity(view));
        ClearScriptNotice(view);
        _scriptMarkers.Remove(_viewIds[view]);
        _scriptedZoomChanges.Remove(ViewIdentity(view));
        _viewIds.RemoveAt(view);
        ObserverPlacesVersion++;
        Layout = Layout.Remove(view)!;
        if (Layout.View is null)
            SplitLayout = Layout.Direction;
        _clocks.OnViewRemoved(view);
    }

    public void Synchronize(bool enabled, int source) => _clocks.Synchronize(enabled, source);

    public void SetDeltaT(DeltaTModel model)
    {
        if (model is < DeltaTModel.EspenakMeeus2006 or > DeltaTModel.FixedOffset)
            throw new ArgumentOutOfRangeException(nameof(model));
        DeltaT = model;
    }

    public void SetMode(int view, CameraMode mode)
    {
        CameraModeRules.ValidateRequestedMode(World, mode);
        var transition = CameraModeRules.Plan(World, _views[view], mode, DeltaT);
        if (transition.Selection is { } selection)
            Select(view, selection);
        if (transition.Epoch is { } epoch)
            Seek(view, epoch);
        CancelFlight(view);
        LeaveShip(view);
        double sharedRate = CurrentPlaybackRate(view);
        _views[view] = _views[view] with { Mode = mode, Navigation = null };
        if (Synchronized && ViewCount > 1)
            SharePlaybackRate(sharedRate);
    }

    public void SetLunarTerrainCamera(int view, LunarTerrainCamera camera)
    {
        if (!camera.IsValid())
            throw new ArgumentOutOfRangeException(nameof(camera));
        _views[view] = _views[view] with { Terrain = camera };
    }

    public void SelectObject(int view, ObjectId selection)
    {
        if (World.Catalogue.Find(selection) is null)
            throw new ArgumentOutOfRangeException(nameof(selection));
        _views[view] = _views[view] with { Selection = selection };
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
        {
            SetClock(view, target.Epoch, previous.Playing);
            LastNavigationEpochChange = new(++_navigationEpochRevision, target.Id, previous.Epoch, target.Epoch, Synchronized);
        }
    }

    public void TogglePlaying(int view) => SetPlaying(view, !_views[view].Playing);

    public void SetPlaying(int view, bool playing) => SetClock(view, _views[view].Epoch, playing);

    // Synchronized views share one clock, so they share its rate. Otherwise the rate would change whenever another view
    // became the clock owner.
    public void SetPlaybackRate(int view, double rate) => SetPlaybackRate(view, rate, synchronizeRate: true);

    internal void SetPlaybackRate(int view, double rate, bool synchronizeRate) =>
        _clocks.SetPlaybackRate(view, rate, synchronizeRate);

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
        _clocks.Step(view, seconds, World.Ephemeris.Start, World.Ephemeris.End);
    }

    internal static double DefaultPlaybackRate(CameraMode mode) => ObserverClock.DefaultPlaybackRate(mode);

    public bool Tick(double elapsed)
    {
        bool changed = ExpireScriptNotices(elapsed);
        int start = Synchronized ? _clocks.Owner : 0;
        int end = Synchronized ? start + 1 : _views.Count;
        for (int view = start; view < end; view++)
        {
            if (_views[view].Playing)
            {
                double rate = CurrentPlaybackRate(view);
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

        var fallbacks = new List<int>();
        for (int index = 0; index < _views.Count; index++)
        {
            ObserverView previous = _views[index];
            ObjectId? selected = previous.Selection is { } id && next.Catalogue.Find(id) is not null
                ? id
                : null;
            bool lostMode = (previous.Mode == CameraMode.Satellites && next.Satellites.Count == 0) ||
                (previous.Mode == CameraMode.Mission && next.MissionId is null) ||
                (previous.Mode == CameraMode.Timeline && next.TimelineId is null) ||
                (previous.Mode == CameraMode.Probe && next.Probe is null) ||
                (previous.Mode == CameraMode.LunarTerrain && !next.HasLunarTerrain) ||
                (previous.Mode == CameraMode.MarsTerrain && !next.HasMarsTerrain);
            CameraMode mode = lostMode
                ? CameraMode.Solar
                : previous.Mode;
            NavigationCamera? navigation = previous.Navigation;
            if (navigation is { Target: { } target } camera)
            {
                bool lostTarget = next.Catalogue.Find(target) is null;
                bool lostFrame = camera.Placement is { UsesFrame: true } &&
                    !camera.TryFrameBasis(next.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT, out _);
                if (lostTarget || lostFrame)
                {
                    NavigationPlacement? placement = camera.Placement is { UsesFrame: true }
                        ? camera.ResolvePlacement(World.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT) : camera.Placement;
                    bool release = lostTarget || camera.Placement?.CatalogueFrame is not null;
                    navigation = camera with { Target = release ? null : target, Tracking = !release && camera.Tracking,
                        Placement = placement };
                    CancelFlight(index);
                }
            }
            ObjectId? aim = previous.ScriptAimTarget is { } tracked && next.Catalogue.Find(tracked) is not null ? tracked : null;
            EarthObserverCamera? site = previous.EarthObserver;
            if (site is { IsEarth: false } surface &&
                !SurfaceObserver.TryFrame(next, surface, next.Graph.Evaluate(previous.Epoch, DeltaT), DeltaT, out _))
            {
                site = previous.Places.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
                if (mode == CameraMode.Surface && navigation is null) mode = CameraMode.Solar;
                fallbacks.Add(index);
            }
            var updated = previous with { Selection = selected, Mode = mode, Navigation = navigation, ScriptAimTarget = aim };
            _views[index] = site is { } location && site != previous.EarthObserver ? updated.WithObserverCamera(location) :
                updated with { EarthObserver = site };
        }
        World = next;
        SurfaceObserverFallbacks = fallbacks.AsReadOnly();
        if (fallbacks.Count > 0) ObserverPlacesVersion++;
        return true;
    }

    private void SetClock(int view, JulianInstant epoch, bool playing) => _clocks.SetClock(view, epoch, playing);

    private void SharePlaybackRate(double rate) => _clocks.SharePlaybackRate(rate);

    private static bool Covers(SimulationWorld world, JulianInstant epoch) =>
        WorldEpochRange.Contains(world.Ephemeris, epoch);
}
