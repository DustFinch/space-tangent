// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Globalization;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Control
{
    private static readonly int NavigationOption = Enum.GetValues<CameraMode>().Length;
    private sealed record ViewControls(PanelContainer Panel, SimulationViewport Renderer, Label Epoch,
        Button Play, OptionButton Mode, OptionButton Selection, LineEdit TimelineEpoch, HBoxContainer TerrainTools,
        LineEdit TerrainCoordinates, SpinBox FieldOfView, Button AdvanceHour, SubViewportContainer Canvas)
    {
        internal WorldSnapshot? Snapshot { get; set; }
    }

    private readonly List<ViewControls> _views = [];
    private AddonGeneration? _addOns;
    private LocalAddonSettings _localAddons = LocalAddonSettings.Empty;
    private LocalAddonSettingsFile _localAddonFile = null!;
    private LocalAddonsPanel _localAddonPanel = null!;
    private IReadOnlyList<PreparedSatellite> Assets => _addOns?.Satellites ?? [];
    private SurfaceTextures? _surfaceTextures;
    private EarthOrbitTextures? _earthOrbitTextures;
    private PlanetTextures? _planetTextures;
    private CheckButton _showPlanetOrbits = null!;
    private bool _refreshingViewField;
    private BrightSkyData? _brightSky;
    private CheckButton _deepSky = null!;
    private CheckButton _stars = null!, _starLabels = null!, _equatorialGrid = null!, _constellations = null!;
    private SimulatorWorkspace _workspace = null!;
    private ObjectSearchPanel _objectSearch = null!;
    private Label _timeHeading = null!, _dataSummary = null!;
    private EarthObserverPanel _earthObserver = null!;
    private Button _satelliteLayersMode = null!;
    private Button _playFromPanel = null!;
    private Label _calendarTime = null!;
    private BoxContainer _panels = null!;
    private StyleBoxFlat _activeFrame = null!, _inactiveFrame = null!;
    private Button _spaceMode = null!, _observatoryMode = null!;
    private Button _split = null!;
    private CheckButton _sync = null!;
    private OptionButton _deltaT = null!;
    private Label _status = null!;
    private int _active;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCancellation;
    private LuaSessionScript? _script;
    private PopupMenu _scriptMenu = null!;
    internal SimulationSession? Session { get; private set; }
    internal bool InitializationFinished { get; private set; }
    internal AddonDiagnostic? LastAddonDiagnostic { get; private set; }

    public override async void _Ready()
    {
        _workspace = new(this, GoToActiveObject, TrackActiveObject);
        _panels = _workspace.Views;
        _status = _workspace.Status;
        _activeFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Highlight, 5, 5);
        _inactiveFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Border, 5, 5);
        _status.Text = "Loading ephemeris…";

        De440Ephemeris ephemeris;
        string ephemerisPath = LocalDataPaths.Find("SPACETANGENT_DE440", "de440.440");
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            ephemeris = await Task.Run(() => De440Ephemeris.Load(ephemerisPath), token);
            if (!CanPublish(generation, token))
                return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!CanPublish(generation, token))
                return;
            _status.Text = "Set SPACETANGENT_DE440 to a classic JPL DE440 file to start the simulation.";
            InitializationFinished = true;
            return;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            if (!CanPublish(generation, token))
                return;
            _status.Text = error.Message;
            InitializationFinished = true;
            return;
        }

        _localAddonFile = new();
        Exception? settingsError = null;
        try
        {
            _localAddons = _localAddonFile.Load();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            settingsError = error;
        }

        try
        {
            _brightSky = BrightSkyData.Load();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning($"Could not load the bright sky catalogue. {error.Message}");
        }
        SimulationWorld world = new(ephemeris, [], brightStars: _brightSky?.Catalogue);
        try
        {
            _status.Text = "Loading add-ons…";
            var decoded = await AddonLoader.Load(ephemeris, null, _localAddons, _localAddonFile, _brightSky?.Catalogue, token);
            if (!CanPublish(generation, token))
                return;
            _addOns = AddonDiagnostics.Read(AddonStage.RenderResources, null, () => AddonLoader.Publish(decoded, null));
            world = decoded.World;
            _status.Text = "";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            if (!CanPublish(generation, token))
                return;
            ShowAddonFailure("Could not load add-ons.", error);
        }
        try
        {
            _surfaceTextures = SurfaceProjection.LoadTextures();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = error.Message;
        }

        try
        {
            _earthOrbitTextures = EarthOrbitTextures.Load(_surfaceTextures);
            _planetTextures = PlanetTextures.Load();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning(error.Message);
        }

        Session = new(world,
            new(JulianInstant.J2000Day, 0, TimeScale.Tdb), ReadDeltaT());
        Session.OpenSpaceView(0);
        Session.SetSkyLayers(0, Session[0].Sky with { Stars = true });
        BuildWorkspaceControls();
        if (settingsError is not null)
            ShowAddonFailure("Could not read local add-on settings.", settingsError);
        AddView();
        Refresh();
        InitializationFinished = true;
        ReportLoadedWorld(world);
        if (OS.GetCmdlineUserArgs().Contains("--smoke-startup"))
            await RunStartupSmoke();
    }

    private void BuildWorkspaceControls()
    {
        var toolbar = _workspace.Toolbar;
        _spaceMode = AddButton(toolbar, "Space", () =>
        {
            Session!.OpenSpaceView(_active);
            Refresh();
        });
        _observatoryMode = AddButton(toolbar, "Observatory", () => SetMode(_active, CameraMode.Surface));
        _spaceMode.Name = "SpaceMode";
        _spaceMode.ToggleMode = _observatoryMode.ToggleMode = true;
        _observatoryMode.Disabled = _surfaceTextures is null;
        _split = AddButton(toolbar, "Split view", ToggleSplit);
        _split.Name = "SplitViews";

        var search = _workspace.AddPage("Search", "Search");
        _objectSearch = new(search, Session!.World.Catalogue, SelectObject);
        var time = _workspace.AddPage("Time", "Time");
        _timeHeading = new Label { ThemeTypeVariation = "WorkspaceTitle" };
        time.AddChild(_timeHeading);
        _calendarTime = SimulatorTheme.Detail("");
        time.AddChild(_calendarTime);
        _playFromPanel = AddButton(time, "Play", () => TogglePlaying(_active));
        _sync = new CheckButton { Name = "SyncViewTime", Text = "Synchronize view time", ButtonPressed = true };
        _sync.Toggled += enabled => Synchronize(enabled, _active);
        time.AddChild(_sync);
        time.AddChild(SimulatorTheme.Section("Go to date"));
        var dateFields = new HBoxContainer();
        time.AddChild(dateFields);
        var utcDate = new LineEdit { Name = "UtcDate", PlaceholderText = "YYYY-MM-DD", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var utcTime = new LineEdit { Name = "UtcTime", PlaceholderText = "HH:MM:SS", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        dateFields.AddChild(utcDate);
        dateFields.AddChild(utcTime);
        utcDate.TextSubmitted += _ => SeekUtc(utcDate.Text, utcTime.Text);
        utcTime.TextSubmitted += _ => SeekUtc(utcDate.Text, utcTime.Text);
        var dateActions = new HBoxContainer();
        time.AddChild(dateActions);
        AddButton(dateActions, "Now", () =>
        {
            DateTime utc = DateTime.UtcNow;
            SeekUtc(utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        });
        AddButton(dateActions, "Go to UTC", () => SeekUtc(utcDate.Text, utcTime.Text));
        var epochInput = new LineEdit { PlaceholderText = "Julian date (TDB)", Name = "TimePanelEpoch" };
        epochInput.TextSubmitted += text => SeekTimeline(_active, text);
        time.AddChild(epochInput);
        time.AddChild(SimulatorTheme.Section("Step"));
        var steps = new HFlowContainer();
        time.AddChild(steps);
        foreach (var (label, seconds) in new[]
                 { ("−1 y", -31_557_600), ("−1 d", -86400), ("−1 h", -3600), ("+1 h", 3600), ("+1 d", 86400), ("+1 y", 31_557_600) })
            AddButton(steps, label, () => Advance(_active, seconds));
        time.AddChild(SimulatorTheme.Section("ΔT model"));
        _deltaT = new OptionButton { Name = "DeltaTModel" };
        _deltaT.AddItem("ΔT · Espenak–Meeus", (int)DeltaTModel.EspenakMeeus2006);
        _deltaT.AddItem("ΔT · Morrison–Stephenson", (int)DeltaTModel.MorrisonStephenson2004);
        _deltaT.AddItem("ΔT · Fixed offset", (int)DeltaTModel.FixedOffset);
        _deltaT.ItemSelected += index => SetDeltaT((DeltaTModel)_deltaT.GetItemId((int)index));
        time.AddChild(_deltaT);

        var layers = _workspace.AddPage("Layers", "Layers and view");
        layers.AddChild(SimulatorTheme.Section("Objects"));
        AddButton(layers, "Inner planets", () => SetMode(_active, CameraMode.Solar));
        AddButton(layers, "Outer planets", () => SetMode(_active, CameraMode.SolarOuter));
        _showPlanetOrbits = new CheckButton { Text = "Planet orbits", ButtonPressed = true };
        _showPlanetOrbits.Toggled += visible =>
        {
            foreach (ViewControls view in _views)
                view.Renderer.ShowPlanetOrbits = visible;
            Refresh();
        };
        layers.AddChild(_showPlanetOrbits);
        AddButton(layers, "Earth–Moon view", () => SetMode(_active, CameraMode.Lunar));
        layers.AddChild(SimulatorTheme.Section("Sky in this view"));
        _stars = SkyToggle(layers, "StarsLayer", "Bright stars");
        _starLabels = SkyToggle(layers, "StarLabelsLayer", "Star labels");
        _equatorialGrid = SkyToggle(layers, "EquatorialGridLayer", "Equatorial grid");
        _constellations = SkyToggle(layers, "ConstellationLayer", "Constellation figures");
        _deepSky = SkyToggle(layers, "DeepSkyLayer", "Deep-sky objects");
        layers.AddChild(SimulatorTheme.Section("Earth satellites"));
        _satelliteLayersMode = AddButton(layers, "Earth satellites view", () => SetMode(_active, CameraMode.Satellites));
        _workspace.AddRailSeparator();
        var location = _workspace.AddPage("Location", "Observer location");
        var ephemerides = _workspace.AddPage("Ephemerides", "Ephemerides");
        _earthObserver = new(location, ephemerides, _surfaceTextures is not null, camera =>
        {
            Session!.SetEarthObserver(_active, camera);
            _status.Text = "";
            SetMode(_active, CameraMode.Surface);
        }, () => ShowFullMoon(_active), message => _status.Text = message);
        ephemerides.VisibilityChanged += () =>
        {
            if (_views.Count > 0 && ephemerides.IsVisibleInTree())
                UpdateActiveFrame();
        };
        _workspace.AddRailSeparator();
        var bookmarks = _workspace.AddPage("Bookmarks", "Scenarios");
        bookmarks.AddChild(SimulatorTheme.Section("Scene file"));
        AddScenarioMenu(bookmarks);
        AddObserverBookmarks(bookmarks);
        var addOns = _workspace.AddPage("AddOns", "Packs and add-ons");
        addOns.AddChild(SimulatorTheme.Section("Loaded inputs"));
        _dataSummary = SimulatorTheme.Detail("");
        addOns.AddChild(_dataSummary);
        _localAddonPanel = new(this, addOns, ReloadAddOns, InstallLocalAddon,
            (kind, enabled) => ReloadAddOns(_localAddons.SetDefault(kind, enabled)),
            (source, enabled) => ReloadAddOns(_localAddons.SetSource(source, enabled)));
        _localAddonPanel.ShowSources(_localAddons, _localAddonFile, _addOns?.Decoded);
        var scripts = _workspace.AddPage("Scripts", "Observer scripts");
        scripts.AddChild(SimulatorTheme.Section("Observer scripts"));
        AddScriptMenu(scripts);
        _workspace.AddRailSeparator();
        _workspace.AddRailSpacer();
        var settings = _workspace.AddPage("Settings", "Settings");
        settings.AddChild(SimulatorTheme.Section("Workspace"));
        AddButton(settings, "Split or close second view", ToggleSplit);
        AddButton(settings, "Views side by side", () => SetSplitLayout(ViewSplit.SideBySide)).Name = "SideBySideViews";
        AddButton(settings, "Stack views", () => SetSplitLayout(ViewSplit.Stacked)).Name = "StackViews";
        AddButton(settings, "Free flight", StartFreeFlight).Name = "FreeFlight";
        AddButton(settings, "Follow body rotation", FollowBodyRotation).Name = "FollowBodyRotation";
        AddButton(settings, "Open time settings", () => _workspace.Open("Time"));
        var help = _workspace.AddPage("Help", "Controls");
        help.AddChild(SimulatorTheme.Detail("Ctrl+U    Split or close the second view"));
        help.AddChild(SimulatorTheme.Detail("Tab    Switch the active view"));
        help.AddChild(SimulatorTheme.Detail("Right drag    Orbit around the camera target"));
        help.AddChild(SimulatorTheme.Detail("Wheel    Zoom the navigation camera"));
        help.AddChild(SimulatorTheme.Detail("Esc    Stop a flight or close the side panel"));
        UpdateLoadedInputs();
        _workspace.Open("Search");
    }

    private void UpdateLoadedInputs()
    {
        var inputs = new List<string> { "JPL DE440" };
        inputs.AddRange(Assets.Select(asset => asset.Track.Name));
        inputs.AddRange(Session!.World.SscBodies.Select(body => body.Definition.Name));
        if (Session!.World.MissionName is { } mission)
            inputs.Add(mission);
        if (Session.World.TimelineId is not null)
            inputs.Add("Cassini sampled timeline");
        if (Session.World.Probe is not null)
            inputs.Add("Huygens timeline");
        if (Session.World.HasLunarTerrain)
            inputs.Add("Lunar elevation");
        _dataSummary.Text = string.Join("\n", inputs);
        _satelliteLayersMode.Disabled = Assets.Count == 0;
    }

    private void SelectObject(ObjectId target)
    {
        Session!.SelectObject(_active, target);
        Refresh();
        _workspace.RevealObjectCard();
    }

    private void GoToActiveObject()
    {
        ObserverView observer = Session![_active];
        if (observer.Selection is { } selected && UseEarthPointing(selected, observer) &&
            Session.World.BrightStars.Find(selected) is { } star)
        {
            PointAtStar(star);
            return;
        }
        _status.Text = Session!.CancelFlight(_active) ? "Flight stopped."
            : Session.GoToSelection(_active) ? "" : "The object has no state at a usable epoch.";
        Refresh();
    }

    private bool UseEarthPointing(ObjectId id, ObserverView view) =>
        Session!.World.BrightStars.Find(id) is not null &&
        ((view.Mode == CameraMode.Surface && view.Navigation is null) || Session.World.Stellar.Find(id) is null);

    private CheckButton SkyToggle(Container panel, string name, string title)
    {
        var button = new CheckButton { Name = name, Text = title, Disabled = _brightSky is null };
        button.Toggled += _ =>
        {
            Session!.SetSkyLayers(_active,
                new SkyLayerSettings(_stars.ButtonPressed, _starLabels.ButtonPressed, _equatorialGrid.ButtonPressed,
                    _constellations.ButtonPressed) { DeepSky = _deepSky.ButtonPressed });
            Refresh();
        };
        panel.AddChild(button);
        return button;
    }

    private void PointAtStar(BrightStar star)
    {
        var observer = Session![_active];
        var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
        if (_views[_active].Snapshot?.SolarSystem is not { } solar || _surfaceTextures is null)
            return;
        try
        {
            var lighting = EarthSurfaceLighting.Sample(solar, camera.Site, Session.DeltaT);
            if (EarthObserving.HorizontalDirection(lighting, star.DirectionAt(observer.Epoch)) is not { } position)
                return;
            Session.SetEarthObserver(_active, camera with
            {
                AzimuthDegrees = position.AzimuthDegrees ?? camera.AzimuthDegrees,
                ElevationDegrees = position.AltitudeDegrees
            });
            Session.SetSkyLayers(_active, observer.Sky with { Stars = true });
            _status.Text = position.AltitudeDegrees >= 0 ? "" : "The star is below this observer's horizon.";
            SetMode(_active, CameraMode.Surface);
        }
        catch (ArgumentOutOfRangeException)
        {
            _status.Text = "The Earth observer is unavailable at this epoch.";
        }
    }

    private void TrackActiveObject()
    {
        _status.Text = Session!.ToggleTracking(_active) ? "" : "The object has no state at a usable epoch.";
        Refresh();
    }

    private void TogglePlaying(int view)
    {
        _active = view;
        Session!.TogglePlaying(view);
        Refresh();
    }

    private void ReportLoadedWorld(SimulationWorld world)
    {
        GD.Print($"SpaceTangent world ready: DE440, {Assets.Count} science satellites, surface view {_surfaceTextures is not null}.");
        if (world.MissionName is { } mission)
            GD.Print($"SpaceTangent mission ready: {mission}.");
        if (world.TimelineId is not null)
            GD.Print("SpaceTangent Cassini timeline ready.");
        if (world.Probe is not null)
            GD.Print("SpaceTangent Huygens ready.");
        if (world.HasLunarTerrain)
            GD.Print("SpaceTangent lunar terrain ready.");
        if (world.SscBodies.Count > 0)
            GD.Print($"SpaceTangent SSC body catalogue ready: {world.SscBodies.Count} bodies.");
    }

    public override void _Input(InputEvent input)
    {
        if (_views.Count < 2 || input is not InputEventKey { Keycode: Key.Tab, Pressed: true, Echo: false,
                CtrlPressed: false, AltPressed: false, MetaPressed: false } ||
            GetViewport().GuiGetFocusOwner() is LineEdit)
            return;
        ActivateView((_active + 1) % _views.Count);
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (Session is null || input is not InputEventKey { Pressed: true } key)
            return;
        if (TryFreeFlightKey(key))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (key.Echo)
            return;
        if (!key.CtrlPressed && !key.AltPressed && !key.MetaPressed &&
            GetViewport().GuiGetFocusOwner() is not LineEdit && TryObserverFrameKey(key))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        switch (key.Keycode)
        {
            case Key.U when key.CtrlPressed && key.ShiftPressed:
                SplitActiveView(ViewSplit.SideBySide);
                break;
            case Key.U when key.CtrlPressed:
                ToggleSplit();
                break;
            case Key.R when key.CtrlPressed:
                SplitActiveView(ViewSplit.Stacked);
                break;
            case Key.D when key.CtrlPressed:
                KeepActiveView();
                break;
            case Key.C when key.CtrlPressed && key.ShiftPressed:
                _workspace.Open("Bookmarks");
                CreateSceneLink(legacy: true);
                break;
            case Key.Delete when Session.ViewCount > 1:
                CloseView(_active);
                break;
            case Key.Enter:
                _workspace.Open("Search");
                _objectSearch.Focus();
                break;
            case Key.Space:
                TogglePlaying(_active);
                break;
            case Key.Escape:
                if (_script is { Finished: false })
                    StopScript();
                else if (Session.CancelFlight(_active))
                {
                    _status.Text = "Flight stopped.";
                    Refresh();
                }
                else
                    _workspace.Open(null);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _script?.Stop();
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    public override void _Process(double delta)
    {
        bool changed = Session?.Tick(delta) == true;
        if (_script is { } script)
        {
            changed |= script.Step(delta);
            if (script.Finished)
            {
                _script = null;
                _scriptMenu.SetItemDisabled(1, true);
                _status.Text = script.Error ?? "Lua script finished.";
            }
        }
        if (changed || Session?.AnyFlight == true)
            Refresh(delta);
    }

    internal SimulationViewport Renderer(int view) => _views[view].Renderer;

    internal void SetMode(int view, CameraMode mode)
    {
        _active = view;
        Session!.SetMode(view, mode);
        Refresh();
    }

    internal void SetLunarTerrainCamera(int view, LunarTerrainCamera camera)
    {
        Session!.SetLunarTerrainCamera(view, camera);
        _active = view;
        Refresh();
    }

    internal void SelectSatellite(int view, ObjectId id)
    {
        _active = view;
        Session!.Select(view, id);
        Refresh();
        _workspace.RevealObjectCard();
    }

    internal void Advance(int view, double seconds)
    {
        _active = view;
        Session!.Step(view, seconds);
        Refresh();
    }

    internal void ShowFullMoon(int view)
    {
        Session!.SetEarthObserver(view, EarthObserverCamera.Moscow);
        SetMode(view, CameraMode.Surface);
        Seek(view, SurfaceProjection.FullMoonEpoch);
    }

    internal void Seek(int view, JulianInstant epoch)
    {
        _active = view;
        Session!.Seek(view, epoch);
        Refresh();
    }

    internal void Synchronize(bool enabled, int source)
    {
        Session!.Synchronize(enabled, source);
        _sync.SetPressedNoSignal(enabled);
        Refresh();
    }

    internal void SetDeltaT(DeltaTModel model)
    {
        Session!.SetDeltaT(model);
        try
        {
            SaveDeltaT(model);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not save ΔT. {error.Message}";
        }
        Refresh();
    }

    internal void ToggleSplit()
    {
        if (Session!.ViewCount == 1)
            SplitActiveView(Session.SplitLayout);
        else
            CloseView(Session.ViewCount == 2 ? 1 : _active);
    }

    private void SplitActiveView(ViewSplit direction)
    {
        if (!Session!.SplitView(_active, direction))
        {
            _status.Text = "The workspace has reached its view limit.";
            return;
        }
        UpdateViewLayout();
        Refresh();
    }

    private void CloseView(int index)
    {
        Session!.RemoveView(index);
        RemoveViewPanel(index);
        if (_active > index)
            _active--;
        _active = Math.Min(_active, Session.ViewCount - 1);
        FinishViewRemoval();
    }

    private void KeepActiveView()
    {
        for (int index = _views.Count - 1; index >= 0; index--)
        {
            if (index == _active)
                continue;
            Session!.RemoveView(index);
            RemoveViewPanel(index);
        }
        _active = 0;
        FinishViewRemoval();
    }

    private void FinishViewRemoval()
    {
        if (_script is { View: < 0 })
            StopScript();
        UpdateViewLayout();
        Refresh();
    }

    private void RemoveViewPanel(int index)
    {
        PanelContainer panel = _views[index].Panel;
        _views.RemoveAt(index);
        panel.GetParent().RemoveChild(panel);
        panel.QueueFree();
    }

    private void SetSplitLayout(ViewSplit layout)
    {
        Session!.SetSplitLayout(layout);
        UpdateViewLayout();
        Refresh();
    }

    private void StartFreeFlight()
    {
        Session!.StartFreeFlight(_active);
        _status.Text = "Free flight · WASD to move, R/F for height, right-drag to turn.";
        Refresh();
    }

    private void FollowBodyRotation()
    {
        _status.Text = Session!.FollowBodyRotation(_active) ? "Following the selected body's rotation."
            : "Select a body with a rotation model at this epoch.";
        Refresh();
    }

    private bool TryObserverFrameKey(InputEventKey key)
    {
        if (key.Keycode == Key.Y)
        {
            FollowBodyRotation();
            return true;
        }
        NavigationFrame frame = key.Unicode switch
        {
            '"' => NavigationFrame.Chase,
            ':' => NavigationFrame.PhaseLock,
            _ => NavigationFrame.Inertial
        };
        if (frame == NavigationFrame.Inertial)
            return false;
        _status.Text = Session!.FollowRelativeFrame(_active, frame) ?
            frame == NavigationFrame.Chase ? "Following the selected body's orbital motion." : "Phase-locked to the selected body and its parent."
            : "Select a body with a usable orbital frame at this epoch.";
        Refresh();
        return true;
    }

    private bool TryFreeFlightKey(InputEventKey key)
    {
        if (key.CtrlPressed || key.AltPressed || key.MetaPressed || GetViewport().GuiGetFocusOwner() is LineEdit)
            return false;
        (double right, double up, double forward) = key.Keycode switch
        {
            Key.A => (-1, 0, 0),
            Key.D => (1, 0, 0),
            Key.W => (0, 0, 1),
            Key.S => (0, 0, -1),
            Key.R => (0, 1, 0),
            Key.F => (0, -1, 0),
            _ => (0, 0, 0)
        };
        if ((right, up, forward) == (0d, 0d, 0d) || !Session!.MoveFreeFlight(_active, right, up, forward))
            return false;
        Refresh();
        return true;
    }

    private void UpdateViewLayout()
    {
        _panels.Vertical = Session!.SplitLayout == ViewSplit.Stacked;
        while (_views.Count < Session.ViewCount)
            AddView();
        while (_views.Count > Session.ViewCount)
            RemoveViewPanel(_views.Count - 1);

        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls view = _views[index];
            view.Panel.GetParent().RemoveChild(view.Panel);
            view.AdvanceHour.Name = $"AdvanceHour{index}";
            view.FieldOfView.Name = $"ViewField{index}";
            view.Canvas.Name = $"ViewCanvas{index}";
        }
        foreach (Node child in _panels.GetChildren())
        {
            _panels.RemoveChild(child);
            child.QueueFree();
        }
        AttachView(Session.Layout, _panels, "");
        _split.Text = Session.ViewCount switch { 1 => "Split view", 2 => "Close second view", _ => "Close active view" };
    }

    private Control AttachView(ObserverLayout layout, Container parent, string path)
    {
        if (layout.View is { } index)
        {
            PanelContainer panel = _views[index].Panel;
            panel.SizeFlagsStretchRatio = 1;
            parent.AddChild(panel);
            return panel;
        }
        bool vertical = layout.Direction == ViewSplit.Stacked;
        var split = new SplitContainer
        {
            Name = "ViewDivider" + path,
            Vertical = vertical,
            DraggerVisibility = SplitContainer.DraggerVisibilityEnum.Hidden,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        parent.AddChild(split);
        Control first = AttachView(layout.First!, split, path + "0");
        Control second = AttachView(layout.Second!, split, path + "1");
        double share = layout.FirstShare;
        split.Resized += () => Callable.From(ApplyShare).CallDeferred();
        split.DragEnded += () => Callable.From(StoreShare).CallDeferred();
        Callable.From(ApplyShare).CallDeferred();
        return split;

        float Extent(Control control) => vertical ? control.Size.Y : control.Size.X;

        void ApplyShare()
        {
            if (!IsInstanceValid(split) || !split.IsInsideTree())
                return;
            float total = Extent(first) + Extent(second);
            if (total <= 0)
                return;
            int offset = Mathf.RoundToInt((float)(total * share) - Extent(first));
            if (offset != 0)
            {
                split.SplitOffsets = [split.SplitOffsets.FirstOrDefault() + offset];
                split.ClampSplitOffset();
            }
        }

        void StoreShare()
        {
            if (!IsInstanceValid(split) || !split.IsInsideTree())
                return;
            float total = Extent(first) + Extent(second);
            if (total <= 0)
                return;
            share = Extent(first) / total;
            Session!.ResizeViewDivider(path, share);
        }
    }

    private void AddScenarioMenu(Container toolbar)
    {
        var menu = new MenuButton { Name = "ScenarioMenu", Text = "Scenario" };
        toolbar.AddChild(menu);
        var open = new FileDialog { Name = "OpenScenario", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        var save = new FileDialog { Name = "SaveScenario", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile, DeletingEnabled = false };
        AddChild(open);
        AddChild(save);
        open.AddFilter("*.stscene,*.cel", "Scenarios and observer favorites");
        save.AddFilter("*.stscene", "SpaceTangent scenarios");
        open.FileSelected += path =>
        {
            if (Path.GetExtension(path).Equals(".cel", StringComparison.OrdinalIgnoreCase))
                ImportFavorites(path);
            else
                LoadScenario(path);
        };
        save.FileSelected += path => SaveScenario(path);
        PopupMenu popup = menu.GetPopup();
        popup.AddItem("Open scenario…", 0);
        popup.AddItem("Save scenario…", 1);
        popup.IdPressed += id => (id == 0 ? open : save).PopupCentered(new Vector2I(700, 460));
    }

    private void AddScriptMenu(Container toolbar)
    {
        var menu = new MenuButton { Name = "ScriptMenu", Text = "Script" };
        toolbar.AddChild(menu);
        var open = new FileDialog { Name = "OpenScript", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        AddChild(open);
        open.AddFilter("*.lua,*.celx", "Lua scripts");
        open.FileSelected += path => RunScript(path);
        _scriptMenu = menu.GetPopup();
        _scriptMenu.AddItem("Run Lua…", 0);
        _scriptMenu.AddItem("Stop script", 1);
        var conversionSource = AddTourDialogs();
        _scriptMenu.AddItem("Convert tour…", 2);
        _scriptMenu.SetItemDisabled(1, true);
        _scriptMenu.IdPressed += id =>
        {
            if (id == 0)
                open.PopupCentered(new Vector2I(700, 460));
            else if (id == 1)
                StopScript();
            else
                conversionSource.PopupCentered(new Vector2I(700, 460));
        };
    }

    internal LuaSessionScript? RunScript(string path)
    {
        try
        {
            LuaSessionScript next = LuaSessionScript.Load(path, Session!, _active, _surfaceTextures is not null, line => GD.Print(line));
            _script?.Stop();
            _script = next;
            _scriptMenu.SetItemDisabled(1, false);
            _status.Text = "Lua script running.";
            return next;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open script. {error.Message}";
            GD.PushWarning(_status.Text);
            return null;
        }
    }

    private void StopScript()
    {
        if (_script is { } running)
        {
            running.Stop();
            if (running.View >= 0)
                Session!.CancelFlight(running.View);
        }
        _script = null;
        _scriptMenu.SetItemDisabled(1, true);
        _status.Text = "Lua script stopped.";
    }

    internal bool SaveScenario(string path)
    {
        try
        {
            ScenarioFile.Save(path, Session!.CaptureScenario(_active));
            _status.Text = "Scenario saved.";
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not save scenario. {error.Message}";
            return false;
        }
    }

    internal bool LoadScenario(string path)
    {
        try
        {
            ApplyScenario(ScenarioFile.Load(path));
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open scenario. {error.Message}";
            return false;
        }
        _status.Text = "Scenario opened.";
        return true;
    }

    private void ApplyScenario(SimulationScenario scenario)
    {
        if (_surfaceTextures is null && scenario.Views.Any(view => view.Mode == CameraMode.Surface && view.Navigation is null))
            throw new InvalidDataException("The scenario needs the Earth surface textures.");
        Session!.RestoreScenario(scenario);
        if (_script is not null)
            StopScript();
        _active = scenario.ActiveView;
        UpdateViewLayout();
        _sync.SetPressedNoSignal(Session.Synchronized);
        Refresh();
    }

    internal Task<bool> ReloadAddOns() => ReloadAddOns(null);

    private async Task InstallLocalAddon(LocalAddonKind kind, string path)
    {
        try
        {
            LocalAddonSource source = _localAddonFile.FindOrCreate(_localAddons, kind, path);
            await ReloadAddOns(_localAddons.SetSource(source, true));
        }
        catch (Exception error) when (IsLoadError(error))
        {
            ShowAddonFailure("Could not add local source.", error);
        }
    }

    private async Task<bool> ReloadAddOns(LocalAddonSettings? requested)
    {
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            _status.Text = "Reloading add-ons…";
            _localAddonPanel.SetBusy(true);
            LocalAddonSettings settings = requested ?? _localAddons;
            AddonGeneration? previous = _addOns;
            var decoded = await AddonLoader.Load(Session!.World.Ephemeris, previous?.Decoded, settings, _localAddonFile, _brightSky?.Catalogue, token);
            if (!CanPublish(generation, token))
                return false;
            AddonGeneration next = AddonDiagnostics.Read(AddonStage.RenderResources, null, () => AddonLoader.Publish(decoded, previous));
            if (requested is not null)
                _localAddonFile.Save(requested);
            if (!ReferenceEquals(previous, next))
            {
                if (!Session.TryReplaceWorld(decoded.World))
                    throw new InvalidOperationException("The new world does not cover the current epochs.");
                _addOns = next;
                _objectSearch.Replace(Session.World.Catalogue);
                UpdateLoadedInputs();
                foreach (ViewControls view in _views)
                    ReplaceAssets(view);
                Refresh();
            }
            _localAddons = settings;
            _status.Text = "Add-ons reloaded.";
            _status.TooltipText = "";
            LastAddonDiagnostic = null;
            GD.Print($"SpaceTangent add-ons reloaded: {Assets.Count} science satellites.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            if (CanPublish(generation, token))
                ShowAddonFailure("Could not reload add-ons.", error);
            return false;
        }
        finally
        {
            if (CanPublish(generation, token))
            {
                _localAddonPanel.SetBusy(false);
                _localAddonPanel.ShowSources(_localAddons, _localAddonFile, _addOns?.Decoded);
            }
        }
    }

    private void ShowAddonFailure(string prefix, Exception error)
    {
        AddonDiagnostics.Annotate(error, AddonStage.World);
        var issue = AddonDiagnostics.Get(error)!;
        LastAddonDiagnostic = issue;
        string? filename = issue.File is null ? null : Path.GetFileName(issue.File);
        if (filename is not null && issue.Line is { } line)
            filename = $"{filename}:{line}";
        string context = string.Join(" · ", new[] { issue.Source, issue.Stage.ToString(), filename }.OfType<string>());
        _status.Text = $"{prefix} {context}. {issue.Detail}";
        _status.TooltipText = issue.File ?? "";
        GD.Print($"SpaceTangent import [{issue.Code}] {context} · {issue.File ?? "-"} · {issue.Detail}");
    }

    private CancellationToken BeginLoad()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        return _loadCancellation.Token;
    }

    private bool CanPublish(int generation, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _loadGeneration &&
        GodotObject.IsInstanceValid(this) && IsInsideTree();

    private void AddView()
    {
        int index = _views.Count;
        var frame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        int CurrentIndex() => _views.FindIndex(view => ReferenceEquals(view.Panel, frame));
        _panels.AddChild(frame);
        var panel = new VBoxContainer();
        frame.AddChild(panel);
        var tools = new HFlowContainer();
        panel.AddChild(tools);
        var epoch = new Label();
        panel.AddChild(epoch);
        AddButton(tools, "−1 h", () => Advance(CurrentIndex(), -3600));
        Button play = AddButton(tools, "Play", () => TogglePlaying(CurrentIndex()));
        Button advanceHour = AddButton(tools, "+1 h", () => Advance(CurrentIndex(), 3600));
        advanceHour.Name = $"AdvanceHour{index}";
        var mode = new OptionButton();
        mode.AddItem("Inner planets", (int)CameraMode.Solar);
        mode.AddItem("Earth–Moon view", (int)CameraMode.Lunar);
        mode.AddItem("Earth satellites", (int)CameraMode.Satellites);
        mode.AddItem("Earth surface", (int)CameraMode.Surface);
        mode.AddItem("Cassini at Saturn", (int)CameraMode.Mission);
        mode.AddItem("Cassini timeline", (int)CameraMode.Timeline);
        mode.AddItem("Huygens", (int)CameraMode.Probe);
        mode.AddItem("Lunar terrain", (int)CameraMode.LunarTerrain);
        mode.AddItem("Outer planets", (int)CameraMode.SolarOuter);
        mode.AddItem("Olympus Mons", (int)CameraMode.MarsTerrain);
        mode.AddItem("Object camera", NavigationOption);
        mode.SetItemDisabled(NavigationOption, true);
        mode.SetItemDisabled((int)CameraMode.Surface, _surfaceTextures is null);
        mode.ItemSelected += item => SetMode(CurrentIndex(), (CameraMode)mode.GetItemId((int)item));
        tools.AddChild(mode);
        var field = new SpinBox { Name = $"ViewField{index}", MinValue = 2, MaxValue = 120, Step = .01,
            Value = Session![index].EffectiveFieldOfView, LocalizeNumeralSystem = false,
            CustomMinimumSize = new Vector2(112, 0),
            Suffix = "° FOV", TooltipText = "Vertical field of view" };
        field.ValueChanged += value =>
        {
            if (_refreshingViewField)
                return;
            Session!.SetFieldOfView(CurrentIndex(), value);
            Refresh();
        };
        tools.AddChild(field);
        var selection = new OptionButton();
        selection.ItemSelected += item => SelectSatellite(CurrentIndex(), Assets[(int)item].Track.Id);
        tools.AddChild(selection);
        var timelineEpoch = new LineEdit { Name = "TimelineEpoch", PlaceholderText = "JD TDB ↵", Visible = false,
            TooltipText = "Enter a TDB Julian date, such as 2453167.5, and press Enter." };
        timelineEpoch.TextSubmitted += text => SeekTimeline(CurrentIndex(), text);
        panel.AddChild(timelineEpoch);

        var terrainTools = new HBoxContainer { Visible = false };
        AddButton(terrainTools, "Globe", () => SetLunarTerrainCamera(CurrentIndex(), LunarTerrainCamera.Globe));
        AddButton(terrainTools, "Copernicus", () => SetLunarTerrainCamera(CurrentIndex(), LunarTerrainCamera.Copernicus));
        var terrainCoordinates = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "Lon°, lat°, width km ↵",
            TooltipText = "East longitude, north latitude and view width in km. Separate values with commas."
        };
        terrainCoordinates.TextSubmitted += text => SeekTerrain(CurrentIndex(), terrainCoordinates, text);
        terrainTools.AddChild(terrainCoordinates);
        panel.AddChild(terrainTools);

        var container = new SubViewportContainer
        {
            Name = $"ViewCanvas{index}",
            Stretch = true,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        container.GuiInput += input => HandleViewInput(CurrentIndex(), input);
        panel.AddChild(container);
        var viewport = new SubViewport { OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        container.AddChild(viewport);
        var renderer = new SimulationViewport();
        viewport.AddChild(renderer);
        if (_surfaceTextures is { } textures)
            renderer.SetSurfaceTextures(textures);
        if (_earthOrbitTextures is { } earthOrbit)
            renderer.SetEarthOrbitTextures(earthOrbit);
        if (_planetTextures is { } planets)
            renderer.SetPlanetTextures(planets);
        renderer.PlanetEphemeris = Session!.World.Ephemeris;
        renderer.ShowPlanetOrbits = _showPlanetOrbits.ButtonPressed;
        if (_brightSky is { } sky)
            renderer.SetSkyData(sky);
        var controls = new ViewControls(frame, renderer, epoch, play, mode, selection, timelineEpoch,
            terrainTools, terrainCoordinates, field, advanceHour, container);
        _views.Add(controls);
        ReplaceAssets(controls);
    }

    private void HandleViewInput(int view, InputEvent input)
    {
        if (input is InputEventMouseButton { Pressed: true })
            ActivateView(view);
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } pick &&
            _views[view].Renderer.PickObject(pick.Position) is { } target)
        {
            SelectObject(target);
            GetViewport().SetInputAsHandled();
            return;
        }
        bool adjusted = input switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp } => Session!.AdjustCamera(view, 0, 0, .8),
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown } => Session!.AdjustCamera(view, 0, 0, 1.25),
            InputEventMouseMotion motion when (motion.ButtonMask & MouseButtonMask.Right) != 0 =>
                Session!.AdjustCamera(view, -.006 * motion.Relative.X, .006 * motion.Relative.Y, 1),
            _ => false
        };
        if (adjusted)
        {
            GetViewport().SetInputAsHandled();
            Refresh();
        }
    }

    private void ReplaceAssets(ViewControls view)
    {
        view.Renderer.ReplaceSatellites(Assets);
        view.Renderer.ReplaceMission(_addOns?.Mission, _addOns?.Timeline, _addOns?.Probe);
        view.Renderer.ReplaceLunarTerrain(_addOns?.Terrain);
        view.Renderer.ReplaceMarsTerrain(_addOns?.MarsTerrain);
        view.Renderer.SetCatalogueWorld(Session!.World);
        view.Renderer.ReplaceSscBodies(_addOns?.SscBodies ?? []);
        view.Selection.Clear();
        foreach (PreparedSatellite asset in Assets)
            view.Selection.AddItem(asset.Track.Name);
        view.Mode.SetItemDisabled((int)CameraMode.Satellites, Assets.Count == 0);
        view.Mode.SetItemDisabled((int)CameraMode.Mission, _addOns?.Mission is null);
        view.Mode.SetItemDisabled((int)CameraMode.Timeline, _addOns?.Timeline is null);
        view.Mode.SetItemDisabled((int)CameraMode.Probe, _addOns?.Probe is null);
        view.Mode.SetItemDisabled((int)CameraMode.LunarTerrain, _addOns?.Terrain is null);
        view.Mode.SetItemDisabled((int)CameraMode.MarsTerrain, _addOns?.MarsTerrain is null);
    }

    private void SeekUtc(string dateText, string clockText)
    {
        string[] clock = clockText.Split(':');
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ||
            clock.Length != 3 || !Parse(clock[0], out int hour) || !Parse(clock[1], out int minute) ||
            !Parse(clock[2], out int second))
        {
            _status.Text = "Enter a UTC date and time as YYYY-MM-DD and HH:MM:SS.";
            return;
        }
        try
        {
            JulianInstant epoch = TimeConversion.FromUtc(new(date, hour, minute, second), TimeScale.Tdb);
            Seek(_active, epoch);
            _status.Text = "";
        }
        catch (ArgumentOutOfRangeException)
        {
            _status.Text = "The UTC date or time is outside the supported calendar or ephemeris.";
        }

        static bool Parse(string value, out int field) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out field);
    }

    private static string UtcLabel(JulianInstant epoch)
    {
        try
        {
            var time = TimeConversion.ToUtc(epoch);
            return FormattableString.Invariant($"{time.Date:yyyy-MM-dd}  {time.Hour:D2}:{time.Minute:D2}:{time.Second:D2} UTC");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "TDB";
        }
    }

    private void SeekTimeline(int view, string text)
    {
        try
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException("Enter a Julian date in TDB, for example 2453167.5.");
            Seek(view, JulianInstant.FromJulianDate(value, TimeScale.Tdb));
            _status.Text = "";
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            _status.Text = $"Could not set the epoch. {error.Message}";
        }
    }

    private void SeekTerrain(int view, LineEdit coordinates, string text)
    {
        string[] fields = text.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 3 && Read(0, out double longitude) && Read(1, out double latitude) && Read(2, out double width))
        {
            var camera = new LunarTerrainCamera(longitude, latitude, width);
            if (camera.IsValid())
            {
                coordinates.ReleaseFocus();
                SetLunarTerrainCamera(view, camera);
                _status.Text = "";
                return;
            }
        }
        _status.Text = "Enter longitude (−180–180°), latitude (−90–90°) and width (50–4600 km), separated by commas.";

        bool Read(int index, out double value) =>
            double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private void ActivateView(int index)
    {
        if (_active == index)
            return;
        _active = index;
        UpdateActiveFrame();
    }

    private void UpdateActiveFrame()
    {
        for (int index = 0; index < _views.Count; index++)
        {
            var frame = _views[index].Panel;
            var style = index == _active ? _activeFrame : _inactiveFrame;
            if (frame.GetThemeStylebox("panel") != style)
                frame.AddThemeStyleboxOverride("panel", style);
        }
        var active = Session![_active];
        bool observatory = active.Mode == CameraMode.Surface && active.Navigation is null;
        _spaceMode.SetPressedNoSignal(!observatory);
        _observatoryMode.SetPressedNoSignal(observatory);
        _workspace.Observation.Text = $"View {_active + 1} · {(active.Playing ? "Playing" : "Paused")}";
        _timeHeading.Text = FormattableString.Invariant($"JD {active.Epoch.ToJulianDate():F6}");
        _calendarTime.Text = UtcLabel(active.Epoch);
        _playFromPanel.Text = active.Playing ? "Pause" : "Play";
        _earthObserver.Update(_active, active, _views[_active].Snapshot, Session.World.Ephemeris, Session.DeltaT, Session.World.BrightStars);
        _stars.SetPressedNoSignal(active.Sky.Stars);
        _starLabels.SetPressedNoSignal(active.Sky.Labels);
        _equatorialGrid.SetPressedNoSignal(active.Sky.EquatorialGrid);
        _constellations.SetPressedNoSignal(active.Sky.Constellations);
        _deepSky.SetPressedNoSignal(active.Sky.DeepSky);
        _objectSearch.UpdateSelection(active.Selection);
        var target = active.Selection is { } selection ? Session.World.Catalogue.Find(selection) : null;
        bool tracking = active.Navigation is { Tracking: true } pose && pose.Target == target?.Id;
        string details = DescribeObject(target, _views[_active].Snapshot);
        bool pointAt = target is not null && UseEarthPointing(target.Id, active);
        _workspace.ShowObject(target, details, tracking, Session.IsFlying(_active), pointAt, _surfaceTextures is not null);
    }

    private string DescribeObject(CatalogueObject? target, WorldSnapshot? snapshot)
    {
        if (target is null)
            return "";
        var details = $"Catalogue ID: {target.Id.Value}";
        if (Session!.World.Stellar.Find(target.Id) is { } spatial)
            return details + FormattableString.Invariant($"\nDistance: {spatial.DistanceParsecs * StellarEntry.KilometersPerParsec / StellarEntry.KilometersPerLightYear:N2} ly\nVisual magnitude: {spatial.VisualMagnitude:F2}");
        if (Session.World.BrightStars.Find(target.Id) is { } bright)
            return details + FormattableString.Invariant($"\nVisual magnitude: {bright.VisualMagnitude:F2}");
        if (DeepSkyCatalogue.Find(target.Id) is { } deep)
            return details + FormattableString.Invariant($"\nDistance: {deep.DistanceLightYears:N0} ly\nEstimated radius: {deep.RadiusLightYears:N0} ly");
        if (snapshot?.SolarSystem is { } solar && snapshot.TryState(target.Id, out var state))
            return details + FormattableString.Invariant($"\nFrom Earth: {(state - solar.Earth).PositionKilometers.Length:N1} km");
        return details + "\nState unavailable at this epoch";
    }

    private void Refresh(double elapsed = 0)
    {
        SimulationSession session = Session!;
        _deltaT.Select((int)session.DeltaT);
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls controls = _views[index];
            var snapshot = session.World.Graph.Evaluate(session[index].Epoch);
            session.UpdateCamera(index, snapshot, elapsed);
            ObserverView view = session[index];
            controls.Snapshot = snapshot;
            bool available = controls.Renderer.Draw(view, snapshot, session.DeltaT);
            controls.Epoch.Text = FormattableString.Invariant($"View {index + 1}   JD {view.Epoch.ToJulianDate():F6} TDB");
            if (session.IsFlying(index))
                controls.Epoch.Text += "   ·   Flying to target";
            else if (view.Navigation is { Tracking: true, Target: { } target })
                controls.Epoch.Text += $"   ·   Tracking {session.World.Catalogue.Find(target)?.Name ?? target.Value}";
            if (!available)
                controls.Epoch.Text += view.Mode switch
                {
                    CameraMode.Mission => "   Cassini state or pointing is outside kernel coverage.",
                    CameraMode.Timeline => "   Cassini is outside the sampled timeline.",
                    CameraMode.Probe => "   Huygens is outside the available timeline or kernel coverage.",
                    CameraMode.LunarTerrain => "   Lunar terrain is unavailable.",
                    CameraMode.MarsTerrain => "   Olympus Mons needs the local Mars data pack.",
                    _ => "   Surface view is available for 1900–2099."
                };
            if (controls.Renderer.ProbeStatus is { } phase)
                controls.Epoch.Text += "\n" + phase;
            controls.Play.Text = view.Playing ? "Pause" : "Play";
            bool preset = view.Navigation is null;
            controls.Mode.Select(preset ? (int)view.Mode : NavigationOption);
            controls.FieldOfView.Visible = !preset || view.Mode == CameraMode.Surface;
            _refreshingViewField = true;
            try
            {
                controls.FieldOfView.Value = view.EffectiveFieldOfView;
            }
            finally
            {
                _refreshingViewField = false;
            }
            controls.Selection.Visible = preset && view.Mode == CameraMode.Satellites;
            controls.TimelineEpoch.Visible = preset && view.Mode is CameraMode.Timeline or CameraMode.Probe;
            controls.TerrainTools.Visible = preset && view.Mode == CameraMode.LunarTerrain;
            if (controls.TerrainTools.Visible && !controls.TerrainCoordinates.HasFocus())
            {
                var camera = view.Terrain ?? LunarTerrainCamera.Globe;
                controls.TerrainCoordinates.Text = FormattableString.Invariant(
                    $"{camera.LongitudeDegrees:G}, {camera.LatitudeDegrees:G}, {camera.FieldKilometers:G}");
            }
            int selected = -1;
            for (int item = 0; item < Assets.Count && selected < 0; item++)
            {
                if (Assets[item].Track.Id == view.Selection)
                    selected = item;
            }
            controls.Selection.Select(selected);
        }
        UpdateActiveFrame();
    }

    private static bool IsLoadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or OverflowException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or NotSupportedException;

    private static string SettingsPath()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH");
        return string.IsNullOrWhiteSpace(configured) ? ProjectSettings.GlobalizePath("user://delta-t.txt") : configured;
    }

    private static DeltaTModel ReadDeltaT()
    {
        try
        {
            if (Enum.TryParse(File.ReadAllText(SettingsPath()), out DeltaTModel selected) && Enum.IsDefined(selected))
                return selected;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A fresh profile has no preference yet.
        }
        return DeltaTModel.EspenakMeeus2006;
    }

    private static void SaveDeltaT(DeltaTModel model)
    {
        string path = SettingsPath();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, model.ToString());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static Button AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += () => onPressed();
        parent.AddChild(button);
        return button;
    }
}
