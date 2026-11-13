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
        LineEdit TerrainCoordinates, SpinBox FieldOfView, Button AdvanceHour, SubViewportContainer Canvas, MeasurementOverlay Measurements)
    {
        internal WorldSnapshot? Snapshot { get; set; }
        internal ProjectionScale? Perspective { get; set; }
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
    private IReadOnlyDictionary<ObjectId, ArrayMesh>? _planetMoonModels;
    private readonly Dictionary<string, Texture2D> _exoplanetTextures = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> _orbitAtmospheres =
        new Dictionary<ObjectId, OrbitAtmosphereTextures>();
    private IReadOnlyDictionary<ObjectId, BodyTileTextures> _surfaceTiles = new Dictionary<ObjectId, BodyTileTextures>();
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
    private ScriptMediaPlayer? _scriptMedia;
    private PopupMenu _scriptMenu = null!;
    internal SimulationSession? Session { get; private set; }
    internal bool InitializationFinished { get; private set; }
    internal AddonDiagnostic? LastAddonDiagnostic { get; private set; }

    public override async void _Ready()
    {
        _workspace = new(this, GoToActiveObject, TrackActiveObject);
        _panels = _workspace.Views;
        _status = _workspace.Status;
        _galacticSky.Published += () => Refresh();
        if (InspectStartupArchive())
            return;
        ConnectCapture();
        _activeFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Highlight, 5, 5);
        _inactiveFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Border, 5, 5);
        _status.Text = "Loading ephemeris…";

        ISolarSystemEphemeris ephemeris;
        string ephemerisPath = LocalDataPaths.Find("SPACETANGENT_DE440", "de440.440");
        LocalDataPath historicalPath = LocalDataPaths.Candidates("SPACETANGENT_VSOP87", "vsop87-legacy.bin");
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            ephemeris = await Task.Run(() => LoadSolarEphemeris(ephemerisPath, historicalPath), token);
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
        AddonLoader.DecodedAddOns? candidate = null;
        try
        {
            _status.Text = "Loading add-ons…";
            var decoded = candidate = await AddonLoader.Load(ephemeris, null, _localAddons, _localAddonFile, _brightSky?.Catalogue, token);
            if (!CanPublish(generation, token))
                return;
            _addOns = AddonDiagnostics.Read(AddonStage.RenderResources, null, () => AddonLoader.Publish(decoded, null));
            _galacticSky.SetDust(decoded.GalacticDust.Value);
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
        finally
        {
            DisposeSupersededStars(candidate);
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
            _orbitAtmospheres = OrbitAtmosphereTextures.LoadAll();
            _surfaceTiles = BodyTileTextures.Load();
            string maps = LocalDataPaths.Find("SPACETANGENT_EXOPLANET_TEXTURES", "sky/exoplanet-textures", directory: true);
            foreach (string profile in world.Exoplanets.Bodies.Select(planet => planet.TextureClass).Distinct(StringComparer.OrdinalIgnoreCase))
                if (PlanetTextures.Read(maps, profile + ".jpg") is { } texture)
                    _exoplanetTextures.Add(profile, texture);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning(error.Message);
        }

        try
        {
            _planetMoonModels = PlanetMoonModels.Load(_planetTextures);
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
        if (_startupArguments.Any(argument => argument.StartsWith("--startup-", StringComparison.Ordinal)))
            await RunStartupRecording();
        else if (_startupArguments.Contains("--smoke-startup"))
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
        }, () => ShowFullMoon(_active), message => _status.Text = message, _earthOrbitTextures?.Day, (name, site) => _ = SaveEarthSite(name, site),
            name => _ = RemoveEarthSite(name));
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
            (source, enabled) => ReloadAddOns(_localAddons.SetSource(source, enabled)), UpdateSatelliteGroups);
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
        if (observer.Selection is { } location && Session.World.EarthPlaces.Find(location) is { } place)
        {
            var camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
            Session.SetEarthObserver(_active, camera with
            {
                Site = place.ObserverSite,
                Conditions = place.Bortle is { } bortle ? new GroundSkyConditions(bortle) : camera.Conditions
            });
            SetMode(_active, CameraMode.Surface);
            _status.Text = place.EnglishName;
            return;
        }
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
                Session[_active].Sky with
                {
                    Stars = _stars.ButtonPressed,
                    Labels = _starLabels.ButtonPressed,
                    EquatorialGrid = _equatorialGrid.ButtonPressed,
                    Constellations = _constellations.ButtonPressed,
                    DeepSky = _deepSky.ButtonPressed
                });
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
                ElevationDegrees = camera.Conditions?.ApparentAltitude(position.AltitudeDegrees, camera.Site) ??
                    position.AltitudeDegrees
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

    private static ISolarSystemEphemeris LoadSolarEphemeris(string de440Path, LocalDataPath historicalPath)
    {
        var precise = De440Ephemeris.Load(de440Path);
        string historical = historicalPath.Resolve();
        if (historicalPath.Explicit is not null && !File.Exists(historical))
            throw new InvalidDataException("The configured historical planetary series file is missing.");
        return File.Exists(historical) ? HistoricalSolarEphemeris.Load(precise, historical) : precise;
    }

    private void ReportLoadedWorld(SimulationWorld world)
    {
        GD.Print($"SpaceTangent world ready: DE440, {Assets.Count} science satellites, surface view {_surfaceTextures is not null}.");
        if (world.Ephemeris is HistoricalSolarEphemeris)
            GD.Print("SpaceTangent historical ephemeris ready: years 1–4000.");
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
        if (input is InputEventKey shipKey && HandleShipInput(shipKey))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_views.Count < 2 || input is not InputEventKey { Keycode: Key.Tab, Pressed: true, Echo: false,
                CtrlPressed: false, AltPressed: false, MetaPressed: false } ||
            GetViewport().GuiGetFocusOwner() is LineEdit)
            return;
        ActivateView((_active + 1) % _views.Count);
        GetViewport().SetInputAsHandled();
    }

    private bool _showSurfaceSites;

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
            GetViewport().GuiGetFocusOwner() is not LineEdit && (TryClockKey(key) || TryObserverFrameKey(key)))
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
            case Key.A when key.CtrlPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                _status.Text = _views[_active].Measurements.ToggleAngle()
                    ? "Angle measurement · drag across the view. Ctrl+A closes the tool." : "Angle measurement closed.";
                break;
            case Key.B when key.CtrlPressed && !key.ShiftPressed && GetViewport().GuiGetFocusOwner() is not LineEdit:
                bool showMarks = Session[_active].ReferenceMarks == BodyReferenceMarks.None;
                Session.SetReferenceMarks(_active, showMarks ? BodyReferenceMarks.All : BodyReferenceMarks.None);
                _status.Text = showMarks ? "Reference marks visible in this view." : "Reference marks hidden.";
                Refresh();
                break;
            case Key.M when key.CtrlPressed:
                MeasureSelectedDistance(key.ShiftPressed);
                break;
            case Key.L when key.CtrlPressed:
                _showSurfaceSites = !_showSurfaceSites;
                _status.Text = _showSurfaceSites ? "Surface locations visible at their map scale." : "Surface location labels hidden.";
                Refresh();
                break;
            case Key.E when key.CtrlPressed && key.ShiftPressed:
                GoToObserverEvent();
                break;
            case Key.E when key.CtrlPressed:
                _ = FindNextObserverEvent();
                break;
            case Key.Key0 when key.CtrlPressed:
                ChangeOptics(cycleTube: key.ShiftPressed, cycleEyepiece: key.AltPressed, flip: false, barlow: false);
                break;
            case Key.Key0 when key.AltPressed && !key.ShiftPressed:
                ChangeOptics(cycleTube: false, cycleEyepiece: false, flip: true, barlow: false);
                break;
            case Key.Key0 when key.ShiftPressed && !key.AltPressed:
                ChangeOptics(cycleTube: false, cycleEyepiece: false, flip: false, barlow: true);
                break;
            case Key.P when key.CtrlPressed && !key.ShiftPressed:
                CycleLandscape(clear: key.AltPressed);
                break;
            case Key.H when key.CtrlPressed && key.ShiftPressed && !key.AltPressed:
                FindMeteorPeak();
                break;
            case Key.H when key.CtrlPressed && key.AltPressed && !key.ShiftPressed:
                GoToMeteorPeak();
                break;
            case Key.H when key.CtrlPressed && !key.ShiftPressed && !key.AltPressed:
                ToggleMeteors();
                break;
            case Key.G when key.CtrlPressed && !key.ShiftPressed:
                ChangeGroundSky(key.AltPressed);
                break;
            case Key.N when key.CtrlPressed && key.AltPressed && !key.ShiftPressed:
                ExportObserverNight();
                break;
            case Key.N when key.CtrlPressed && key.ShiftPressed && !key.AltPressed:
                GoToDarkSample();
                break;
            case Key.N when key.CtrlPressed && !key.ShiftPressed && !key.AltPressed:
                _ = FindObserverNight();
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
            case Key.F10 when key.ShiftPressed && !key.CtrlPressed && !key.AltPressed:
                ToggleRecording();
                break;
            case Key.F10 when !key.ShiftPressed && !key.CtrlPressed && !key.AltPressed:
                QueueScreenshot();
                break;
            case Key.Delete when Session.ViewCount > 1:
                CloseView(_active);
                break;
            case Key.Enter:
                _workspace.Open("Search");
                _objectSearch.Focus();
                break;
            case Key.Space when GetViewport().GuiGetFocusOwner() is not Button:
                TogglePlaying(_active);
                break;
            case Key.Escape:
                if (Replaying)
                {
                    CloseReplay();
                    Refresh();
                }
                else if (_script is { Finished: false } || _scriptMedia?.Active == true)
                    StopScript();
                else if (Session.CancelFlight(_active))
                {
                    _status.Text = "Flight stopped.";
                    Refresh();
                }
                else if (!ExitShip())
                    _workspace.Open(null);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _galacticSky.Dispose();
        _addOns?.Decoded.DistantStars.Value?.Dispose();
        _eventSearch?.Cancel();
        _landscapeLifetime.Cancel();
        _satelliteUpdate?.Cancel();
        DisconnectCapture();
        DisablePlugins(updateMenu: false);
        _script?.Stop();
        _scriptMedia?.Clear();
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
            _shipKeys.Clear();
    }

    public override void _Process(double delta)
    {
        if (AdvanceReplay(delta))
            return;
        if (MovieActive)
            delta = 1.0 / WindowRecording.FramesPerSecond;
        FlyShip(delta);
        bool changed = Session?.Tick(delta) == true;
        changed |= _demoSurfaces.PublishReady();
        foreach (var view in _views)
            changed |= view.Renderer.PublishDemoTerrain();
        changed |= UpdatePerspectiveScale();
        changed |= StepPlugins(delta);
        if (_script is { } script)
        {
            changed |= script.Step(delta);
            if (script.Finished)
            {
                _script = null;
                _scriptMenu.SetItemDisabled(1, true);
                if (script.Error is not null)
                    _scriptMedia?.Clear();
                _status.Text = script.Error ?? "Lua script finished.";
            }
        }
        if (_scriptMedia is { } media)
        {
            if (!media.Attached)
            {
                _scriptMedia = null;
                _scriptMenu.SetItemDisabled(1, true);
            }
            else if (_script is null)
                _scriptMenu.SetItemDisabled(1, !media.Active);
        }
        if (changed || Session is { } session && (session.AnyFlight || session.HasShips))
            Refresh(delta);
    }

    internal SimulationViewport Renderer(int view) => _views[view].Renderer;
    internal MeasurementOverlay Measurements(int view) => _views[view].Measurements;

    private void MeasureSelectedDistance(bool clear)
    {
        var overlay = _views[_active].Measurements;
        if (clear)
        {
            overlay.ClearDistance();
            _status.Text = "Distance measurement cleared.";
            return;
        }
        if (Session![_active].Selection is not { } selected)
        {
            _status.Text = "Select an object to measure from or to.";
            return;
        }
        overlay.SelectDistancePoint(selected);
        Refresh();
        _status.Text = overlay.WaitingForSecondPoint ? "Select the second object and press Ctrl+M." :
            overlay.DistanceKilometers.HasValue ? overlay.DistanceText : "The two positions are unavailable at this epoch.";
    }

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
        if (key.Keycode == Key.G)
        {
            if (key.ShiftPressed && Session!.ApproachProceduralSurface(_active))
            {
                _status.Text = "Approaching the selected surface.";
                Refresh();
            }
            else
                GoToActiveObject();
            return true;
        }
        if (key.Keycode == Key.C)
        {
            var observer = Session![_active];
            if (observer.Selection is { } id && UseEarthPointing(id, observer) &&
                Session.World.BrightStars.Find(id) is { } star)
                PointAtStar(star);
            else
            {
                _status.Text = Session.FocusSelection(_active) ? "Centering the selected object." : "The object cannot be centered from this position.";
                Refresh();
            }
            return true;
        }
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
        ResetPerspectiveScale();
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
        open.AddFilter("*.stscene,*.cel,*.json", "Scenarios and observer saves");
        open.AddFilter("*.stframes", "Observer frame recordings");
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
        open.AddFilter("plugin.json", "Lua plugin manifest");
        open.FileSelected += path =>
        {
            if (Path.GetFileName(path).Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                OpenPlugin(path);
            else
                RunScript(path);
        };
        _scriptMenu = menu.GetPopup();
        _scriptMenu.AddItem("Run Lua…", 0);
        _scriptMenu.AddItem("Stop script", 1);
        _tourConversionSource = AddTourDialogs();
        _scriptMenu.AddItem("Convert tour…", 2);
        _scriptMenu.SetItemDisabled(1, true);
        _scriptMenu.IdPressed += id =>
        {
            if (id == 0)
                open.PopupCentered(new Vector2I(700, 460));
            else if (id == 1)
                StopScript();
            else if (id == 2)
                _tourConversionSource.PopupCentered(new Vector2I(700, 460));
            else
                RunPluginMenuAction(id);
        };
    }

    internal LuaSessionScript? RunScript(string path)
    {
        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var media = new ScriptMediaPlayer(Renderer(_active));
            LuaSessionScript next = LuaSessionScript.Load(path, Session!, _active, _surfaceTextures is not null,
                line => GD.Print(line), request => media.Present(directory, request));
            _scriptMedia?.Clear();
            _scriptMedia = media;
            if (_script is { } previous)
            {
                previous.Stop();
                if (previous.View >= 0)
                    Session!.ClearScriptNotice(previous.View);
            }
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
            {
                Session!.ClearScriptNotice(running.View);
                Session.CancelFlight(running.View);
                _views[running.View].Renderer.SetScriptNotice(null);
            }
        }
        _script = null;
        _scriptMenu.SetItemDisabled(1, true);
        _scriptMedia?.Clear();
        _scriptMedia = null;
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
        string extension = Path.GetExtension(path);
        if (extension.Equals(".stframes", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".celframe", StringComparison.OrdinalIgnoreCase))
            return StartReplay(path);
        try
        {
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var input = File.OpenRead(path);
                if (ObserverBookmarkFile.IsArchivedCollection(input))
                    return ImportFavorites(path);
                input.Position = 0;
                CloseReplay();
                ApplyScenario(SceneLink.ReadArchivedObserver(input, Session!.World, Session.DeltaT));
            }
            else
            {
                CloseReplay();
                ApplyScenario(ScenarioFile.Load(path));
            }
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
        ObserverLayout previousLayout = Session!.Layout;
        ViewSplit previousDirection = Session.SplitLayout;
        Session.RestoreScenario(scenario);
        ResetPerspectiveScale();
        if (_script is not null)
            StopScript();
        _active = scenario.ActiveView;
        if (Session.Layout != previousLayout || Session.SplitLayout != previousDirection)
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

    internal async Task<bool> ReloadAddOns(LocalAddonSettings? requested)
    {
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        AddonLoader.DecodedAddOns? candidate = null;
        try
        {
            _status.Text = "Reloading add-ons…";
            _localAddonPanel.SetBusy(true);
            LocalAddonSettings settings = requested ?? _localAddons;
            AddonGeneration? previous = _addOns;
            var decoded = candidate = await AddonLoader.Load(Session!.World.Ephemeris, previous?.Decoded, settings, _localAddonFile, _brightSky?.Catalogue, token);
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
                DisposeSupersededStars(previous?.Decoded);
                _galacticSky.SetDust(decoded.GalacticDust.Value);
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
            DisposeSupersededStars(candidate);
            if (CanPublish(generation, token))
            {
                _localAddonPanel.SetBusy(false);
                _localAddonPanel.ShowSources(_localAddons, _localAddonFile, _addOns?.Decoded);
            }
        }
    }

    private void DisposeSupersededStars(AddonLoader.DecodedAddOns? decoded)
    {
        if (decoded?.DistantStars.Value is { } index && !ReferenceEquals(index, _addOns?.Decoded.DistantStars.Value))
            index.Dispose();
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
        var renderer = new SimulationViewport
        {
            GalacticCache = _galacticSky, DemoSurfaces = _demoSurfaces, CompactTables = _compactLensTables,
            CinematicCompactLens = _startupArguments.Contains("--startup-cinematic-lens", StringComparer.Ordinal)
        };
        viewport.AddChild(renderer);
        if (_surfaceTextures is { } textures)
            renderer.SetSurfaceTextures(textures);
        if (_earthOrbitTextures is { } earthOrbit)
            renderer.SetEarthOrbitTextures(earthOrbit, _orbitAtmospheres.GetValueOrDefault(SolarSystemSample.EarthId));
        if (_planetTextures is { } planets)
            renderer.SetPlanetTextures(planets);
        if (_planetMoonModels is { } models)
            renderer.SetPlanetMoonModels(models);
        renderer.SetWorldAtmospheres(_orbitAtmospheres, _surfaceTiles);
        renderer.SetExoplanetTextures(_exoplanetTextures);
        renderer.PlanetEphemeris = Session!.World.Ephemeris;
        renderer.ShowPlanetOrbits = _showPlanetOrbits.ButtonPressed;
        if (_brightSky is { } sky)
            renderer.SetSkyData(sky);
        var measurements = new MeasurementOverlay { Name = "Measurements" };
        viewport.AddChild(measurements);
        var controls = new ViewControls(frame, renderer, epoch, play, mode, selection, timelineEpoch,
            terrainTools, terrainCoordinates, field, advanceHour, container, measurements);
        _views.Add(controls);
        ReplaceAssets(controls);
    }

    private void HandleViewInput(int view, InputEvent input)
    {
        if (input is InputEventMouseButton { Pressed: true })
            ActivateView(view);
        var measurements = _views[view].Measurements;
        if (measurements.HandleAngleInput(input))
        {
            if (measurements.AngleDegrees is { } angle)
                _status.Text = "Angle " + MeasurementOverlay.FormatAngle(angle);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } pick &&
            _views[view].Renderer.PickObject(pick.Position) is { } target)
        {
            SelectObject(target);
            GetViewport().SetInputAsHandled();
            return;
        }
        bool adjusted = input switch
        {
            InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel =>
                AdjustWheel(view, wheel),
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

    private bool AdjustWheel(int view, InputEventMouseButton wheel)
    {
        var session = Session!;
        var observer = session[view];
        bool inward = wheel.ButtonIndex == MouseButton.WheelUp;
        if (wheel.CtrlPressed || observer is { Mode: CameraMode.Surface, Navigation: null })
        {
            double factor = inward ? .85 : 1 / .85;
            session.SetFieldOfView(view, Math.Clamp(observer.EffectiveFieldOfView * factor, 2, 120));
            return true;
        }

        return session.AdjustCamera(view, 0, 0, inward ? .8 : 1.25);
    }

    private readonly GalacticTextureCache _galacticSky = new();
    private readonly DemoSurfaceCache _demoSurfaces = new();
    private readonly CompactLensTables _compactLensTables = new();

    private void ReplaceAssets(ViewControls view)
    {
        view.Renderer.ReplaceSatellites(Assets);
        view.Renderer.ReplaceMission(_addOns?.Mission, _addOns?.Timeline, _addOns?.Probe);
        view.Renderer.ReplaceLunarTerrain(_addOns?.Terrain);
        view.Renderer.ReplaceMarsTerrain(_addOns?.MarsTerrain);
        view.Renderer.SetCatalogueWorld(Session!.World);
        view.Renderer.SetDeepSkySprites(_addOns?.Decoded.DeepSky.Value.Sprites);
        view.Renderer.ReplaceImportedBodies(_addOns?.ImportedBodies ?? []);
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

    private static string CivilTimeLabel(JulianInstant epoch, DeltaTModel model)
    {
        try
        {
            var time = TimeConversion.ToUtc(epoch);
            return FormattableString.Invariant($"{time.Date:yyyy-MM-dd}  {time.Hour:D2}:{time.Minute:D2}:{time.Second:D2} UTC");
        }
        catch (ArgumentOutOfRangeException)
        {
            return EarthCivilTime.ModeledUt1(epoch, model) is { } modeled
                ? FormattableString.Invariant($"{modeled.Date:yyyy-MM-dd}  {modeled.Time:HH:mm:ss} UT1 (modeled)") : "TDB";
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
        _shipKeys.Clear();
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
        _calendarTime.Text = CivilTimeLabel(active.Epoch, Session!.DeltaT);
        _playFromPanel.Text = active.Playing ? "Pause" : "Play";
        _earthObserver.Update(_active, active, _views[_active].Snapshot, Session.World.Ephemeris, Session.DeltaT,
            Session.World.BrightStars, CurrentObserverNight(), Session.World.EarthPlaces);
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
        SimulationWorld world = Session!.World;
        var details = $"Catalogue ID: {target.Id.Value}";
        if (world.EarthPlaces.Find(target.Id) is { } place)
        {
            string region = place.Region.Length == 0 ? "" : "\n" + place.Region;
            string darkness = place.Bortle is { } bortle ? FormattableString.Invariant($"\nBortle {bortle}") : "";
            string zone = place.TimeZone.Length == 0 ? "" : "\n" + place.TimeZone;
            return details + region + FormattableString.Invariant(
                $"\nLatitude {place.LatitudeDegrees:F4}° · Longitude {place.LongitudeDegrees:F4}°\nAltitude {place.AltitudeMeters:G} m") + darkness + zone;
        }
        if (world.FindSurfaceSite(target.Id) is { } site)
        {
            var parent = world.Catalogue.Find(site.Body)?.Name ?? site.Body.Value;
            return details + FormattableString.Invariant($"\nOn {parent}\n{site.Definition.LatitudeDegrees:F2}° latitude, {site.Definition.LongitudeDegrees:F2}° longitude");
        }
        if (world.FindStellarTarget(target.Id) is { } spatial)
        {
            details += FormattableString.Invariant($"\nDistance: {spatial.DistanceParsecs * StellarEntry.KilometersPerParsec / StellarEntry.KilometersPerLightYear:N2} ly");
            return spatial.IsBarycenter ? details + "\nSystem barycenter" :
                details + FormattableString.Invariant($"\nVisual magnitude: {spatial.VisualMagnitude:F2}");
        }
        if (world.BrightStars.Find(target.Id) is { } bright)
            return details + FormattableString.Invariant($"\nVisual magnitude: {bright.VisualMagnitude:F2}");
        if (world.DeepSky.Find(target.Id) is { } deep)
            return details + FormattableString.Invariant($"\nDistance: {deep.DistanceLightYears:N0} ly\nEstimated radius: {deep.RadiusLightYears:N0} ly");
        if (world.Exoplanets.Find(target.Id) is { } planet)
            return details + FormattableString.Invariant($"\nRadius: {planet.RadiusKilometers:N0} km\nHost: {world.Catalogue.Find(planet.Host)?.Name ?? planet.Host.Value}");
        if (snapshot?.SolarSystem is { } solar && snapshot.TryState(target.Id, out var state))
            return details + FormattableString.Invariant($"\nFrom Earth: {(state - solar.Earth).PositionKilometers.Length:N1} km");
        return details + "\nState unavailable at this epoch";
    }

    private void Refresh(double elapsed = 0)
    {
        RefreshMissionViewpoints();
        SimulationSession session = Session!;
        _deltaT.Select((int)session.DeltaT);
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls controls = _views[index];
            var snapshot = session.World.Graph.Evaluate(session[index].Epoch);
            session.UpdateCamera(index, snapshot, elapsed);
            session.UpdateShip(index, snapshot, elapsed);
            ShipView? ship = session.Ship(index);
            ObserverView view = session[index];
            controls.Snapshot = snapshot;
            controls.Renderer.ShowSurfaceSiteLabels = _showSurfaceSites;
            controls.Renderer.SetLandscape(ViewLandscape(view));
            controls.Renderer.ArchivedGeometry = index == 0 ? (_replay as LegacyCameraSequence)?.CurrentGeometry : null;
            bool available = controls.Renderer.Draw(view, snapshot, session.DeltaT);
            controls.Renderer.DrawShip(ship, view.Navigation);
            controls.Renderer.SetScriptNotice(session.Notice(index));
            controls.Renderer.DrawScriptMarkers(view, snapshot, session.Markers(index), session.DeltaT);
            controls.Renderer.SetMissionAnnotations(_addOns?.Decoded.SampledMissions.Annotations ?? [], session[index].Epoch);
            controls.Renderer.DrawReferenceMarks(view, session.ReferenceGeometry(index, snapshot));
            controls.Measurements.Update(controls.Renderer, view, snapshot, session.World, session.DeltaT);
            controls.Epoch.Text = FormattableString.Invariant($"View {index + 1}   JD {view.Epoch.ToJulianDate():F6} TDB");
            if (view.LightTimeDelayed)
                controls.Epoch.Text += "   ·   Light time";
            if (ship is not null)
            {
                string targetName = ship.Target is { } aim ? session.World.Catalogue.Find(aim)?.Name ?? aim.Value : "—";
                controls.Epoch.Text += FormattableString.Invariant($"   ·   Ship {ship.Pilot} · {ship.SpeedKilometersPerSecond:G4} km/s · {targetName}");
            }
            else if (session.IsFlying(index))
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
                    _ => "   Surface view is unavailable at this epoch."
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
