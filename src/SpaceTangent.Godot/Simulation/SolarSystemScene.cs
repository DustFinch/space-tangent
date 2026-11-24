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

using SpaceTangent.Application.Addons;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Control
{
    private static readonly int NavigationOption = ObserverViewHost.NavigationOption;
    private readonly ObserverViewHost _viewHost = new();
    private List<ViewControls> _views => _viewHost.Views;
    private ObserverNavigationInput? _navigationInput;
    private ObserverNavigationInput Navigation => _navigationInput ??= new(this, _viewHost);
    private AddonGeneration? _addOns;
    private LocalAddonSettings _localAddons = LocalAddonSettings.Empty;
    private LocalAddonSettingsFile _localAddonFile = null!;
    private LocalAddonsPanel _localAddonPanel = null!;
    private IReadOnlyList<PreparedSatellite> Assets => _addOns?.Satellites ?? [];
    private SurfaceTextures? _surfaceTextures;
    private EarthOrbitTextures? _earthOrbitTextures;
    private PlanetTextures? _planetTextures;
    private Texture2D? _groundRingAtlas;
    private Texture2DArray? _groundSurfaceMaps;
    private IReadOnlyDictionary<ObjectId, ArrayMesh>? _planetMoonModels;
    private readonly Dictionary<string, Texture2D> _exoplanetTextures = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> _orbitAtmospheres =
        new Dictionary<ObjectId, OrbitAtmosphereTextures>();
    private IReadOnlyDictionary<ObjectId, BodyTileTextures> _surfaceTiles = new Dictionary<ObjectId, BodyTileTextures>();
    private bool _viewSizeChanged;
    private BrightSkyData? _brightSky;
    private SimulatorWorkspace _workspace = null!;
    private ObjectSearchPanel _objectSearch = null!;
    private Label _dataSummary = null!;
    private EarthObserverPanel _earthObserver = null!;
    private Button _spaceMode = null!, _observatoryMode = null!;
    private Button _split = null!;
    private ObserverLayersPanel _layersPanel = null!;
    private ObserverTimePanel _timePanel = null!;
    private OptionButton _deltaT => _timePanel.Model;
    private Label _status = null!;
    private int _active { get => _viewHost.Active; set => _viewHost.Active = value; }
    private readonly SharedResourceRetirement<DistantStarIndex> _addonResources = new();
    private readonly AddonLoadCoordinator _addonLoads = new();
    private int _loadGeneration => _addonLoads.Generation;
    private ObserverScriptPlayer? _scriptPlayer;
    private ObserverScriptPlayer Scripts => _scriptPlayer ??= new(Session!, Renderer, ApplyClassicView,
        QueueScriptScreenshot, text => _status.Text = text, enabled => _scriptMenu.SetItemDisabled(1, !enabled));
    private PopupMenu _scriptMenu = null!;
    internal SimulationSession? Session { get; private set; }
    internal bool InitializationFinished { get; private set; }
    internal float LensRenderScale { get; set; } = 1;
    internal AddonDiagnostic? LastAddonDiagnostic { get; private set; }

    public override async void _Ready()
    {
        // Godot cannot await _Ready, so an unexpected exception after an await would otherwise vanish into the
        // synchronization context and leave a half-built window.
        try
        {
            await ReadyAsync();
        }
        catch (Exception error)
        {
            GD.PushError($"SpaceTangent startup failed: {error}");
            if (_status is not null)
                _status.Text = "Startup failed: " + error.Message;
            InitializationFinished = true;
        }
    }

    private async Task ReadyAsync()
    {
        using var startupProfile = RuntimeProfile.Measure("startup.total");
        if (float.TryParse(System.Environment.GetEnvironmentVariable("SPACETANGENT_LENS_RENDER_SCALE"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out float lensScale) && lensScale is >= .5f and <= 1f)
            LensRenderScale = lensScale;
        _workspace = new(this, GoToActiveObject, TrackActiveObject);
        _status = _workspace.Status;
        _galacticSky.Published += () => Refresh();
        if (InspectStartupArchive())
            return;
        ConnectCapture();
        var activeFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Highlight, 5, 5);
        var inactiveFrame = SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Border, 5, 5);
        _status.Text = "Loading ephemeris…";

        ISolarSystemEphemeris ephemeris;
        string ephemerisPath = LocalDataPaths.Find("SPACETANGENT_DE440", "de440.440");
        AddonSourcePath historicalPath = LocalDataPaths.Candidates("SPACETANGENT_VSOP87", "vsop87-legacy.bin");
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            ephemeris = await RuntimeProfile.RunAsync("startup.ephemeris", () => Task.Run(() => LoadSolarEphemeris(ephemerisPath, historicalPath), token));
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
            _brightSky = RuntimeProfile.Run("startup.bright-sky", BrightSkyData.Load);
            _brightSky.Surveys.SetOffline(_localAddons.HipsOffline);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning($"Could not load the bright sky catalogue. {error.Message}");
        }
        SimulationWorld world = new(ephemeris, [], brightStars: _brightSky?.Catalogue);
        AddonPreparation.DecodedAddOns? candidate = null;
        try
        {
            _status.Text = "Loading add-ons…";
            var decoded = candidate = await RuntimeProfile.RunAsync("startup.addons", () =>
                AddonLoader.Load(ephemeris, null, _localAddons, _localAddonFile, _brightSky?.Catalogue, token));
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
            _surfaceTextures = await RuntimeProfile.RunAsync("startup.surface", () =>
                SurfaceProjection.LoadTexturesAsync(token, () => CanPublish(generation, token)));
        }
        catch (OperationCanceledException) when (!CanPublish(generation, token)) { return; }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = error.Message;
        }

        try
        {
            _earthOrbitTextures = await RuntimeProfile.RunAsync("startup.earth-orbit", () =>
                EarthOrbitTextures.LoadAsync(_surfaceTextures, token, () => CanPublish(generation, token)));
            _planetTextures = await RuntimeProfile.RunAsync("startup.planets", () => PlanetTextures.LoadAsync(token,
                () => CanPublish(generation, token), async () => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame)));
            _groundRingAtlas = RuntimeProfile.Run("startup.ring-atlas", _planetTextures.CreateGroundRingAtlas);
            _groundSurfaceMaps = RuntimeProfile.Run("startup.ground-array", _planetTextures.CreateGroundSurfaceArray);
            _orbitAtmospheres = RuntimeProfile.Run("startup.atmospheres", OrbitAtmosphereTextures.LoadAll);
            _surfaceTiles = RuntimeProfile.Run("startup.tiles", BodyTileTextures.Load);
            string maps = LocalDataPaths.Find("SPACETANGENT_EXOPLANET_TEXTURES", "sky/exoplanet-textures", directory: true);
            foreach (string profile in world.Exoplanets.Bodies.Select(planet => planet.TextureClass).Distinct(StringComparer.OrdinalIgnoreCase))
                if (PlanetTextures.Read(maps, profile + ".jpg") is { } texture)
                    _exoplanetTextures.Add(profile, texture);
        }
        catch (OperationCanceledException) when (!CanPublish(generation, token))
        {
            return;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning(error.Message);
        }

        try
        {
            _planetMoonModels = RuntimeProfile.Run("startup.moon-models", () => PlanetMoonModels.Load(_planetTextures));
        }
        catch (Exception error) when (IsLoadError(error))
        {
            GD.PushWarning(error.Message);
        }

        Session = new(world,
            new(JulianInstant.J2000Day, 0, TimeScale.Tdb), ReadDeltaT());
        Session.OpenSpaceView(0);
        Session.SetSkyLayers(0, Session[0].Sky with { Stars = true });
        Navigation.Initialize(Session, new(ActivateView, SelectObject, ChangeViewMagnification,
            UpdatePointerReadout, text => _status.Text = text, () => Refresh()));
        _viewHost.Initialize(Session, _workspace.Views,
            new(Advance, TogglePlaying, SetMode, SetViewField, SelectSatelliteItem, SeekTimeline,
                SetLunarTerrainCamera, SeekTerrain, HandleViewInput, () => _viewSizeChanged = true),
            CreateViewRenderer, BindViewRenderer, ReplaceAssets, RemoveViewMedia,
            activeFrame, inactiveFrame, _surfaceTextures is not null);
        BuildWorkspaceControls();
        if (settingsError is not null)
            ShowAddonFailure("Could not read local add-on settings.", settingsError);
        AddView();
        Refresh();
        RestoreStartupWorkspace();
        RestoreObserverPlaces();
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
        _spaceMode = AddButton(toolbar, WorkspaceText.Choose("Space", "Космос"), () =>
        {
            Session!.OpenSpaceView(_active);
            Refresh();
        });
        _observatoryMode = AddButton(toolbar, WorkspaceText.Choose("Observatory", "Обсерватория"), () => SetMode(_active, CameraMode.Surface));
        _spaceMode.Name = "SpaceMode";
        _observatoryMode.Name = "ObservatoryMode";
        _spaceMode.ToggleMode = _observatoryMode.ToggleMode = true;
        _observatoryMode.Disabled = _surfaceTextures is null;
        _split = AddButton(toolbar, "Split view", ToggleSplit);
        _split.Name = "SplitViews";

        var search = _workspace.AddPage("Search", WorkspaceText.Choose("Search and view", "Поиск и вид"), tabTitle: WorkspaceText.Choose("Search", "Поиск"));
        _objectSearch = new(search, Session!.World.Catalogue, SelectObject, AimAtSkyMarker,
            NavigateFromSearch, SearchNavigationAction);
        var location = _workspace.AddPage("Location", WorkspaceText.Choose("Observing", "Наблюдения"), tabTitle: WorkspaceText.Choose("Location", "Место"));
        _timePanel = new(_workspace, _viewHost,
            new(TogglePlaying, Synchronize, SeekUtc, SeekTimeline, Advance, SetDeltaT, UpdateActiveFrame), CivilTimeLabel);
        BuildAlmanacControls();
        BuildObserverCharts();

        _layersPanel = new(_workspace,
            new(_brightSky is not null, _brightSky?.ZodiacalMap is not null, _brightSky?.MinorBodies.Length is > 0),
            new(mode => SetMode(_active, mode), SetPlanetOrbits, SetSmallBodies, ToggleSkyMark, SetStarlink, SetSkyVisibility));
        var ephemerides = _workspace.AddPage("Ephemerides", WorkspaceText.Choose("Ephemerides", "Эфемериды"), "Location");
        _earthObserver = new(location, ephemerides, _surfaceTextures is not null, camera =>
        {
            if (!SurfaceObserver.TryFrame(Session!.World, camera,
                Session.World.Graph.Evaluate(Session[_active].Epoch, Session.DeltaT), Session.DeltaT, out _))
            {
                _status.Text = "This body's state or orientation is unavailable at the current epoch.";
                Refresh();
                return;
            }
            Session!.SetEarthObserver(_active, camera);
            if (camera.IsEarth && _surfaceTextures is null)
            {
                Session.OpenSpaceView(_active);
                Refresh();
                _status.Text = "Observer site updated. Install Earth surface maps for its sky view.";
                return;
            }
            _status.Text = "";
            SetMode(_active, CameraMode.Surface);
        }, () => ShowFullMoon(_active), message => _status.Text = message, _earthOrbitTextures?.Day, (name, site) => _ = SaveEarthSite(name, site),
            name => _ = RemoveEarthSite(name));
        BuildTonightControls(ephemerides);
        ephemerides.VisibilityChanged += () =>
        {
            if (_views.Count > 0 && ephemerides.IsVisibleInTree())
                UpdateActiveFrame();
        };
        _workspace.AddRailSeparator();
        var bookmarks = _workspace.AddPage("Bookmarks", WorkspaceText.Choose("Scenarios", "Сценарии"));
        bookmarks.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Scene file", "Файл сцены")));
        AddScenarioMenu(bookmarks);
        AddObserverBookmarks(bookmarks);
        var addOns = _workspace.AddPage("AddOns", WorkspaceText.Choose("Packs and add-ons", "Паки и дополнения"));
        addOns.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Loaded inputs", "Загруженные данные")));
        _dataSummary = SimulatorTheme.Detail("");
        addOns.AddChild(_dataSummary);
        _localAddonPanel = new(this, addOns, ReloadAddOns, InstallLocalAddon,
            (kind, enabled) => ReloadAddOns(ApplyPackOverride(_localAddons.SetDefault(kind, enabled), AddonLoader.DefaultPath(kind), enabled)),
            (source, enabled) => ReloadAddOns(ApplyPackOverride(_localAddons.SetSource(source, enabled), AddonLoader.SourcePath(source, _localAddonFile), enabled)), UpdateSatelliteGroups, SetSurveyOffline);
        _localAddonPanel.ShowSources(_localAddons, _localAddonFile, _addOns?.Decoded);
        var scripts = _workspace.AddPage("Scripts", WorkspaceText.Choose("Observer scripts", "Скрипты наблюдателя"), "Bookmarks", WorkspaceText.Choose("Scripts", "Скрипты"));
        scripts.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Observer scripts", "Скрипты наблюдателя")));
        AddScriptMenu(scripts);
        _workspace.AddRailSpacer();
        var settings = _workspace.AddPage("Settings", WorkspaceText.Choose("Settings", "Настройки"));
        settings.AddChild(SimulatorTheme.Section("Workspace"));

        AddButton(settings, "Views side by side", () => SetSplitLayout(ViewSplit.SideBySide)).Name = "SideBySideViews";
        AddButton(settings, "Stack views", () => SetSplitLayout(ViewSplit.Stacked)).Name = "StackViews";
        AddButton(settings, "Free flight", StartFreeFlight).Name = "FreeFlight";
        AddButton(settings, "Follow body rotation", FollowBodyRotation).Name = "FollowBodyRotation";
        AddButton(settings, WorkspaceText.Choose("Open time settings", "Открыть настройки времени"), () => _workspace.Open("Time"));
        var help = _workspace.AddPage("Help", WorkspaceText.Choose("Controls", "Управление"), "Settings");
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
        _layersPanel.SetSatelliteAvailability(Assets.Count > 0);
    }

    private void SelectObject(ObjectId target)
    {
        Session!.SelectObject(_active, target);
        Refresh();
    }

    private (string Label, bool Enabled) SearchNavigationAction(ObjectId target)
    {
        if (Session!.World.EarthPlaces.Find(target) is not null)
            return (WorkspaceText.Choose("Go to observing site", "Перейти к месту наблюдения"), true);
        return UseEarthPointing(target, Session[_active])
            ? (WorkspaceText.Choose("Point at", "Навести камеру"), _surfaceTextures is not null || Session[_active].EarthObserver?.IsEarth == false)
            : (WorkspaceText.Choose("Go to", "Перелететь к объекту"), true);
    }

    private void NavigateFromSearch(ObjectId target)
    {
        if (Session![_active].Selection == target)
            NavigateToActiveObject(stopCurrentFlight: false);
    }

    private void GoToActiveObject() => NavigateToActiveObject(stopCurrentFlight: true);

    private void NavigateToActiveObject(bool stopCurrentFlight)
    {
        ObserverView observer = Session![_active];
        if (observer.Selection is { } location && Session.World.EarthPlaces.Find(location) is { } place)
        {
            var camera = observer.ObserverCamera;
            Session.SetEarthObserver(_active, camera with
            {
                Site = place.ObserverSite,
                Body = null,
                CivilClock = new(place.Name, place.TimeZone, place.FixedOffsetHours),
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
        _status.Text = stopCurrentFlight && Session!.CancelFlight(_active) ? "Flight stopped."
            : Session.GoToSelection(_active) ? "" : "The object has no state at a usable epoch.";
        Refresh();
    }

    private bool UseEarthPointing(ObjectId id, ObserverView view) =>
        Session!.World.BrightStars.Find(id) is not null &&
        ((view.Mode == CameraMode.Surface && view.Navigation is null) || Session.World.Stellar.Find(id) is null);

    private void SetPlanetOrbits(bool visible)
    {
        var session = Session!;
        session.SetSkyLayers(_active, session[_active].Sky with { HideOrbits = !visible });
        Refresh();
    }

    private void SetSmallBodies(MinorBodyDisplay mode)
    {
        var session = Session!;
        session.SetSkyLayers(_active, session[_active].Sky with { SmallBodies = mode });
        Refresh();
    }

    private void ToggleSkyMark(SkyMarkings mark)
    {
        var session = Session!;
        var sky = session[_active].Sky;
        session.SetSkyLayers(_active, sky with { Markings = sky.Markings ^ mark });
        Refresh();
    }

    private void SetStarlink(bool enabled)
    {
        var session = Session!;
        session.SetSkyLayers(_active, session[_active].Sky with { Starlink = enabled });
        Refresh();
    }

    private void SetSkyVisibility(ObserverSkyVisibility visible)
    {
        var session = Session!;
        session.SetSkyLayers(_active, session[_active].Sky with
        {
            Stars = visible.Stars, Labels = visible.Labels, EquatorialGrid = visible.EquatorialGrid,
            Constellations = visible.Constellations, DeepSky = visible.DeepSky, ZodiacalLight = visible.ZodiacalLight
        });
        Refresh();
    }

    private void PointAtStar(BrightStar star)
    {
        var observer = Session![_active];
        var camera = observer.ObserverCamera;
        if (_views[_active].Snapshot is not { SolarSystem: { } solar } snapshot || (camera.IsEarth && _surfaceTextures is null))
            return;
        try
        {
            if (!SurfaceObserver.TryFrame(Session.World, camera, snapshot, Session.DeltaT, out var frame)) return;
            var direction = star.DirectionAt(observer.Epoch);
            if (camera.ApparentPlaces)
                direction = ApparentSky.Aberrate(direction, ApparentSky.ObserverBeta(camera.IsEarth ? solar.Earth : frame.Observer));
            if (frame.Direction(direction) is not { } position)
                return;
            Session.SetEarthObserver(_active, camera with
            {
                AzimuthDegrees = position.AzimuthDegrees ?? camera.AzimuthDegrees,
                ElevationDegrees = (camera.IsEarth ? camera.Conditions?.ApparentAltitude(position.AltitudeDegrees, camera.Site) : null) ??
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

    private static ISolarSystemEphemeris LoadSolarEphemeris(string de440Path, AddonSourcePath historicalPath)
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
        if (world.Catalogue.DuplicateIds.Count > 0)
            GD.PushWarning("SpaceTangent catalogue: later definitions of " +
                string.Join(", ", world.Catalogue.DuplicateIds.Take(10).Select(id => id.Value)) +
                (world.Catalogue.DuplicateIds.Count > 10 ? $" and {world.Catalogue.DuplicateIds.Count - 10} more" : "") +
                " were ignored because an earlier source defined the same ID.");
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
        Navigation.HandleGlobalInput(input);
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
            case Key.M when key.AltPressed && !key.CtrlPressed && !key.ShiftPressed && !key.MetaPressed &&
                Session[_active] is { Mode: CameraMode.Surface, Navigation: null } surface:
                var mount = surface.ObserverCamera;
                Session.SetEarthObserver(_active, mount with { EquatorialMount = !mount.EquatorialMount });
                _status.Text = mount.EquatorialMount ? "Altazimuth mount." : "Equatorial mount.";
                Refresh();
                break;
            case Key.M when key.CtrlPressed:
                MeasureSelectedDistance(key.ShiftPressed);
                break;
            case Key.L when key.CtrlPressed:
                var layers = Session![_active].Sky;
                bool visible = !layers.SurfacePlaces;
                Session.SetSkyLayers(_active, layers with { SurfacePlaces = visible });
                _status.Text = visible ? "Surface locations visible at their map scale." : "Surface location labels hidden.";
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
            case Key.N when key.AltPressed && !key.CtrlPressed && !key.ShiftPressed && !key.MetaPressed:
                Session.NightMode = !Session.NightMode;
                _status.Text = Session.NightMode ? "Red night mode enabled." : "Red night mode disabled.";
                UpdateNightMode(Session.NightMode);
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
            case Key.C when key.AltPressed && !key.CtrlPressed && !key.ShiftPressed && !key.MetaPressed:
                var pointerSky = Session[_active].Sky;
                Session.SetSkyLayers(_active, pointerSky with { PointerCoordinates = !pointerSky.PointerCoordinates });
                Refresh();
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
                else if (_scriptPlayer?.Active == true)
                    StopScript();
                else if (StopCameraMotion(_active, out bool stoppedFlight))
                {
                    _status.Text = stoppedFlight ? "Flight stopped." : "Camera motion stopped.";
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
        FlushObserverPlaces();
        _galacticSky.Dispose();
        _brightSky?.Surveys.Dispose();
        _addonResources.Retire(_addOns?.Decoded.DistantStars.Value);
        _eventSearch.Dispose();
        _almanacSearch.Dispose();
        _chartSearch.Dispose();
        _tonightSearch.Dispose();
        _landscapeLifetime.Cancel();
        _satelliteUpdate?.Cancel();
        DisconnectCapture();
        DisablePlugins(updateMenu: false);
        _scriptPlayer?.Close();
        _addonLoads.Dispose();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            _shipKeys.Clear();
            Navigation.FocusLost();
        }
    }

    public override void _Process(double delta)
    {
        AdvanceObserverPlaces(delta);
        if (AdvanceReplay(delta))
            return;
        if (MovieActive)
            delta = 1.0 / WindowRecording.FramesPerSecond;
        FlyShip(delta);
        bool changed = Session?.Tick(delta) == true || PathsRequireImmediate;
        if (Session is not null)
        {
            changed |= StepCameraInertia(delta);
            changed |= StepFreeFlight(delta);
        }
        changed |= _demoSurfaces.PublishReady();
        changed |= _brightSky?.Surveys.PublishRemote() == true;
        foreach (var view in _views)
        {
            changed |= view.Renderer.PublishDemoTerrain();
            changed |= view.Renderer.HasPendingPaths;
        }
        changed |= UpdatePerspectiveScale();
        changed |= StepPlugins(delta);
        changed |= _scriptPlayer?.Step(delta) == true;
        bool resized = _viewSizeChanged;
        _viewSizeChanged = false;
        if (resized || changed || Session is { } session && (session.AnyFlight || session.HasShips || session.HasActiveAutoOrbit))
            Refresh(delta);
    }

    internal SimulationViewport Renderer(int view) => _views[view].Renderer;
    internal WorldSnapshot? ViewSnapshot(int view) => _views[view].Snapshot;
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
        ClearCameraInertia(_views[view]);
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
    }

    internal void Advance(int view, double seconds)
    {
        _active = view;
        Session!.Step(view, seconds);
        Refresh();
    }

    internal void ShowFullMoon(int view)
    {
        Session!.SetEarthObserver(view, EarthObserverCamera.GreenwichFullMoon);
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
        _viewHost.Close(index);
        FinishViewRemoval();
    }

    private void KeepActiveView()
    {
        _viewHost.KeepActive();
        FinishViewRemoval();
    }

    private void FinishViewRemoval()
    {
        if (_scriptPlayer?.Current is { View: < 0 })
            StopScript();
        UpdateViewLayout();
        Refresh();
    }

    private void RemoveViewPanel(int index) => _viewHost.RemovePanel(index);

    private void RemoveViewMedia(SimulationViewport renderer) => _scriptPlayer?.RemoveView(renderer);

    private void SetSplitLayout(ViewSplit layout)
    {
        Session!.SetSplitLayout(layout);
        UpdateViewLayout();
        Refresh();
    }

    private void StartFreeFlight()
    {
        Session!.StartFreeFlight(_active);
        _status.Text = "Free flight · WASD to move, Q/E or R/F for height, Shift to accelerate, right-drag to turn.";
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
        Key physical = key.PhysicalKeycode == Key.None ? key.Keycode : key.PhysicalKeycode;
        if (physical is Key.Comma or Key.Period && !Session!.InShip(_active))
        {
            ChangeViewMagnification(_active, physical == Key.Comma, .8);
            Refresh();
            return true;
        }
        if (key.Keycode == Key.O && !Session!.InShip(_active))
        {
            _status.Text = Session.ToggleAutoOrbit(_active)
                ? Session[_active].AutoOrbit is { Enabled: true } ? "Automatic orbit enabled." : "Automatic orbit stopped."
                : "Select a target in a space view to orbit.";
            Refresh();
            return true;
        }
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

    private bool TryFreeFlightKey(InputEventKey key) => Navigation.TryFreeFlightKey(key);

    private bool StepFreeFlight(double delta) => Navigation.StepFreeFlight(delta);

    private void UpdateViewLayout()
    {
        ResetPerspectiveScale();
        _viewHost.Relayout();
        _split.Text = Session!.ViewCount switch
        {
            1 => WorkspaceText.Choose("Split view", "Разделить вид"),
            2 => WorkspaceText.Choose("Close second view", "Закрыть второй вид"),
            _ => WorkspaceText.Choose("Close active view", "Закрыть активный вид")
        };
    }

    private void AddScenarioMenu(Container toolbar)
    {
        var menu = new MenuButton { Name = "ScenarioMenu", Text = WorkspaceText.Choose("Scenario", "Сценарий") };
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
        popup.AddItem(WorkspaceText.Choose("Open scenario…", "Открыть сценарий…"), 0);
        popup.AddItem(WorkspaceText.Choose("Save scenario…", "Сохранить сценарий…"), 1);
        popup.AddSeparator();
        popup.AddItem(WorkspaceText.Choose("Import observer settings…", "Импортировать настройки наблюдателя…"), 2);
        popup.AddItem(WorkspaceText.Choose("Use current views at startup", "Открывать эти виды при запуске"), 3);
        popup.AddItem(WorkspaceText.Choose("Clear saved startup views", "Очистить виды запуска"), 4);
        var import = new FileDialog { Name = "ImportObserverSettings", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        import.AddFilter("*.json", "Observer settings");
        AddChild(import);
        import.FileSelected += async path =>
        {
            try { await ImportStartupSettings(path); }
            catch (Exception error) when (IsLoadError(error)) { _status.Text = $"Could not import observer settings. {error.Message}"; }
        };
        popup.IdPressed += id =>
        {
            switch (id)
            {
                case 0: open.PopupCentered(new Vector2I(700, 460)); break;
                case 1: save.PopupCentered(new Vector2I(700, 460)); break;
                case 2: import.PopupCentered(new Vector2I(700, 460)); break;
                case 3: SaveStartupWorkspace(); break;
                case 4: ClearStartupWorkspace(); break;
            }
        };
    }

    private void AddScriptMenu(Container toolbar)
    {
        var menu = new MenuButton { Name = "ScriptMenu", Text = WorkspaceText.Choose("Script", "Скрипт") };
        toolbar.AddChild(menu);
        var open = new FileDialog { Name = "OpenScript", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        AddChild(open);
        open.AddFilter("*.lua,*.celx", "Lua scripts");
        open.AddFilter("plugin.json", "Lua plugin manifest");
        open.FileSelected += path =>
        {
            if (Path.GetFileName(path).Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                OpenPlugin(path, reenable: true);
            else
                RunScript(path);
        };
        _scriptMenu = menu.GetPopup();
        _scriptMenu.AddItem(WorkspaceText.Choose("Run Lua…", "Запустить Lua…"), 0);
        _scriptMenu.AddItem(WorkspaceText.Choose("Stop script", "Остановить скрипт"), 1);
        _tourConversionSource = AddTourDialogs();
        _scriptMenu.AddItem(WorkspaceText.Choose("Convert tour…", "Преобразовать экскурсию…"), 2);
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

    private int ApplyClassicView(ScriptViewRequest request)
    {
        var session = Session!;
        int index = request.View;
        switch (request.Operation)
        {
            case ScriptViewOperation.Split:
                if (session.Layout.ViewSpan(index, request.Direction) >= .2 &&
                    session.SplitView(index, request.Direction, _active, request.Share, request.Direction == ViewSplit.Stacked))
                    UpdateViewLayout();
                break;
            case ScriptViewOperation.Delete when session.Layout.FirstSiblingView(index) is { } sibling:
                session.RemoveView(index);
                RemoveViewPanel(index);
                _shipKeys.Clear();
                Navigation.ClearKeys();
                _active = sibling > index ? sibling - 1 : sibling;
                UpdateViewLayout();
                break;
            case ScriptViewOperation.Activate:
                ActivateView(index);
                break;
            case ScriptViewOperation.Keep when session.ViewCount > 1:
                for (int view = session.ViewCount - 1; view >= 0; view--)
                {
                    if (view == index)
                        continue;
                    session.RemoveView(view);
                    RemoveViewPanel(view);
                }
                _shipKeys.Clear();
                Navigation.ClearKeys();
                _active = 0;
                UpdateViewLayout();
                break;
        }
        return _active;
    }

    internal LuaSessionScript? RunScript(string path)
    {
        try
        {
            return Scripts.Run(path, _active, _surfaceTextures is not null);
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open script. {error.Message}";
            GD.PushWarning(_status.Text);
            return null;
        }
    }

    private void StopScript() => Scripts.Stop();
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
            using var input = new ObserverWorkspaceReader(path);
            if (input.IsFavorites) return ImportFavorites(path);
            CloseReplay();
            ApplyScenario(input.ReadScenario(Session!.World, Session.DeltaT));
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
        if (_surfaceTextures is null && scenario.Views.Any(view => view.Mode == CameraMode.Surface && view.Navigation is null && view.EarthObserver?.IsEarth != false))
            throw new InvalidDataException("The scenario needs the Earth surface textures.");
        ObserverLayout previousLayout = Session!.Layout;
        ViewSplit previousDirection = Session.SplitLayout;
        Session.RestoreScenario(scenario);
        Navigation.ClearKeys();
        foreach (var view in _views)
            ClearCameraInertia(view);
        ResetPerspectiveScale();
        if (_scriptPlayer?.Current is not null)
            StopScript();
        _active = scenario.ActiveView;
        if (Session.Layout != previousLayout || Session.SplitLayout != previousDirection)
            UpdateViewLayout();

        Refresh();
    }

    internal bool SurveysOffline => _brightSky?.Surveys.Offline ?? _localAddons.HipsOffline;

    private void SetSurveyOffline(bool offline)
    {
        try
        {
            var next = _localAddons with { HipsOffline = offline };
            _localAddonFile.Save(next);
            _localAddons = next;
            _brightSky?.Surveys.SetOffline(offline);
            _status.Text = offline ? WorkspaceText.Choose("Sky surveys use local files and cache only.", "Обзоры неба используют только локальные файлы и кэш.")
                : WorkspaceText.Choose("Sky survey downloads enabled.", "Загрузка обзоров неба из сети включена.");
            Refresh();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = error.Message;
            _localAddonPanel.ShowSources(_localAddons, _localAddonFile, _addOns?.Decoded);
        }
    }

    internal Task<bool> ReloadAddOns() => ReloadAddOns(null);

    private LocalAddonSettings ApplyPackOverride(LocalAddonSettings settings, AddonSourcePath path, bool enabled) =>
        enabled && _addOns?.Decoded.FilteredPacks.TryGetValue(path, out var blocked) == true
            ? settings with { DisabledPacks = settings.DisabledPacks.Except(blocked, StringComparer.Ordinal).ToArray() }
            : settings;

    private async Task InstallLocalAddon(AddonSourceKind kind, string path)
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
        using var reloadProfile = RuntimeProfile.Measure("addons.reload");
        var originalDeltaT = Session!.DeltaT;
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        AddonPreparation.DecodedAddOns? candidate = null;
        try
        {
            _status.Text = "Reloading add-ons…";
            _localAddonPanel.SetBusy(true);
            LocalAddonSettings settings = requested ?? _localAddons;
            AddonGeneration? previous = _addOns;
            using var previousUse = _addonResources.Borrow(previous?.Decoded.DistantStars.Value);
            var decoded = candidate = await AddonLoader.Load(Session!.World.Ephemeris, previous?.Decoded, settings, _localAddonFile, _brightSky?.Catalogue, token);
            if (!CanPublish(generation, token))
                return false;
            AddonGeneration next = AddonDiagnostics.Read(AddonStage.RenderResources, null, () => AddonLoader.Publish(decoded, previous));
            int[] surfaceFallbacks = [];
            if (!ReferenceEquals(previous, next))
            {
                Action? persist = requested is null ? null : () => _localAddonFile.Save(requested);
                if (!Session.TryReplaceWorld(decoded.World, persist))
                    throw new InvalidOperationException("The new world does not cover the current epochs.");
                surfaceFallbacks = Session.SurfaceObserverFallbacks.ToArray();
                _addOns = next;
                _galacticSky.SetDust(decoded.GalacticDust.Value);
                _objectSearch.Replace(Session.World.Catalogue);
                UpdateLoadedInputs();
                foreach (ViewControls view in _views)
                    ReplaceAssets(view);
                Refresh();
                // Retire the previous index after every live view has switched successfully.
                DisposeSupersededStars(previous?.Decoded);
            }
            else if (requested is not null)
                _localAddonFile.Save(requested);
            _localAddons = settings;
            _brightSky?.Surveys.SetOffline(settings.HipsOffline);
            _status.Text = "Add-ons reloaded." + (surfaceFallbacks.Length == 0 ? "" :
                " Reset unavailable sites: " + string.Join(", ", surfaceFallbacks.Select(index => index + 1)) + ".");
            _status.TooltipText = "";
            LastAddonDiagnostic = null;
            GD.Print($"SpaceTangent add-ons reloaded: {Assets.Count} science satellites. " +
                $"surfaceResets={surfaceFallbacks.Length}; " +
                $"DeltaT={Session.DeltaT}; preserved={Session.DeltaT == originalDeltaT}; synchronized={Session.Synchronized}; " +
                string.Join("; ", Enumerable.Range(0, Session.ViewCount).Select(index =>
                    FormattableString.Invariant($"view[{index}] camera={Session[index].Mode} target={Session[index].Selection} JD={Session[index].Epoch.ToJulianDate():F9}"))));
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

    private void DisposeSupersededStars(AddonPreparation.DecodedAddOns? decoded)
    {
        if (decoded?.DistantStars.Value is { } index && !ReferenceEquals(index, _addOns?.Decoded.DistantStars.Value))
            _addonResources.Retire(index);
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

    private CancellationToken BeginLoad() => _addonLoads.Begin();

    private bool CanPublish(int generation, CancellationToken token) =>
        _addonLoads.IsCurrent(generation, token) &&
        GodotObject.IsInstanceValid(this) && IsInsideTree();

    private void AddView() => _viewHost.AddView();

    private SimulationViewport CreateViewRenderer() => new()
    {
        GalacticCache = _galacticSky, DemoSurfaces = _demoSurfaces, CompactTables = _compactLensTables,
        CinematicCompactLens = _startupArguments.Contains("--startup-cinematic-lens", StringComparer.Ordinal)
    };

    private void BindViewRenderer(SimulationViewport renderer)
    {
        if (_surfaceTextures is { } textures)
            renderer.SetSurfaceTextures(textures, _groundRingAtlas);
        if (_earthOrbitTextures is { } earthOrbit)
            renderer.SetEarthOrbitTextures(earthOrbit, _orbitAtmospheres.GetValueOrDefault(SolarSystemSample.EarthId));
        if (_planetTextures is { } planets)
            renderer.SetPlanetTextures(planets, _groundSurfaceMaps);
        if (_planetMoonModels is { } models)
            renderer.SetPlanetMoonModels(models);
        renderer.SetWorldAtmospheres(_orbitAtmospheres, _surfaceTiles);
        renderer.SetExoplanetTextures(_exoplanetTextures);
        renderer.PlanetEphemeris = Session!.World.Ephemeris;
        if (_brightSky is { } sky)
            renderer.SetSkyData(sky);
    }

    private void SetViewField(int view, double value)
    {
        Session!.SetFieldOfView(view, value);
        Refresh();
    }

    private void SelectSatelliteItem(int view, int item) => SelectSatellite(view, Assets[item].Track.Id);

    private void HandleViewInput(int view, InputEvent input) => Navigation.HandleViewInput(view, input);

    private bool StepCameraInertia(double elapsed) => Navigation.StepCameraInertia(elapsed);

    private bool StopCameraMotion(int view, out bool stoppedFlight) => Navigation.StopCameraMotion(view, out stoppedFlight);

    private static void ClearCameraInertia(ViewControls controls) => ObserverNavigationInput.ClearCameraInertia(controls);

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
        if (!HistoricalDate.TryParse(dateText, out HistoricalDate date, out long dateTime) || dateTime != 0 ||
            clock.Length != 3 || !Parse(clock[0], out int hour) || !Parse(clock[1], out int minute) ||
            !Parse(clock[2], out int second) || hour > 23 || minute > 59 || second > 60)
        {
            _status.Text = "Enter a date as YYYY-MM-DD (Julian before 1582-10-15) and a time as HH:MM:SS.";
            return;
        }
        try
        {
            // UTC exists from 1972 on. Earlier dates are read as UT1 from the selected ΔT model.
            JulianInstant epoch;
            if (date.DayNumber >= UtcStartDayNumber)
                epoch = TimeConversion.FromUtc(new(date.ToGregorian(), hour, minute, second), TimeScale.Tdb);
            else if (second < 60)
                epoch = EarthCivilTime.FromModeledUt1(date,
                    ((hour * 60L + minute) * 60 + second) * JulianInstant.NanosecondsPerSecond, Session!.DeltaT);
            else
                throw new ArgumentOutOfRangeException(nameof(clockText), "Leap seconds exist only in UTC.");
            Seek(_active, epoch);
            _status.Text = "";
        }
        catch (ArgumentOutOfRangeException)
        {
            _status.Text = "The date or time is outside the supported calendar or ephemeris.";
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
            return EarthCivilTime.ModeledHistoricalUt1(epoch, model) is { } modeled
                ? FormattableString.Invariant(
                    $"{modeled.Date}  {modeled.Time:HH:mm:ss} UT1 (modeled{(modeled.Date.IsJulian ? ", Julian" : "")})")
                : "TDB";
        }
    }

    // 1972 January 1, the first day of UTC with leap seconds.
    private const long UtcStartDayNumber = 2_441_318;

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
        Navigation.ClearKeys();
        _active = index;
        UpdateActiveFrame();
    }

    private void UpdateActiveFrame()
    {
        _viewHost.UpdateActiveStyle();
        var active = Session![_active];
        bool observatory = active.Mode == CameraMode.Surface && active.Navigation is null;
        _spaceMode.SetPressedNoSignal(!observatory);
        _observatoryMode.SetPressedNoSignal(observatory);
        _observatoryMode.Disabled = _surfaceTextures is null && active.EarthObserver?.IsEarth != false;
        _timePanel.Update(_active, active, Session.Synchronized, _views.Count, Session.DeltaT);
        _earthObserver.Update(_active, active, _views[_active].Snapshot, Session.World.Ephemeris, Session.DeltaT,
            Session.World.BrightStars, CurrentObserverNight(), Session.World.EarthPlaces, Session.World);
        _layersPanel.Update(active.Sky);
        _objectSearch.UpdateSelection(active.Selection);
        var target = active.Selection is { } selection ? Session.World.Catalogue.Find(selection) : null;
        bool tracking = active.Navigation is { Tracking: true } pose && pose.Target == target?.Id;
        string details = DescribeObject(target, _views[_active].Snapshot);
        bool pointAt = target is not null && UseEarthPointing(target.Id, active);
        _workspace.ShowObject(target, details, tracking, Session.IsFlying(_active), pointAt,
            _surfaceTextures is not null || active.EarthObserver?.IsEarth == false);
    }

    private string DescribeObject(CatalogueObject? target, WorldSnapshot? snapshot)
    {
        if (target is null)
            return "";
        SimulationWorld world = Session!.World;
        var details = $"Catalogue ID: {target.Id.Value}";
        if (PlanetEphemerides.CenterAt(world.Planets, target.Id, Session[_active].Epoch) is { } center)
            details += "\n" + (center.ApproximateBarycenter
                ? WorkspaceText.Choose("Approximate: system barycenter; centre correction unavailable: ",
                    "Приблизительно: барицентр системы; нет поправки центра: ")
                : WorkspaceText.Choose("Position unavailable; missing kernel or coverage: ",
                    "Положение недоступно; нет ядра или покрытия: ")) + center.RequiredKernel;
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

    private long _reportedNavigationEpoch;

    private void Refresh(double elapsed = 0)
    {
        RefreshMissionViewpoints();
        SimulationSession session = Session!;
        if (session.LastNavigationEpochChange is { } jump && jump.Revision != _reportedNavigationEpoch)
        {
            _reportedNavigationEpoch = jump.Revision;
            string name = session.World.Catalogue.Find(jump.Target)?.Name ?? jump.Target.Value;
            _status.Text = name + ": " + WorkspaceText.Choose("orbit unavailable at the previous epoch; ",
                "орбита недоступна в прежнюю эпоху; ") +
                (jump.Shared ? WorkspaceText.Choose("shared clocks moved to ", "общие часы переведены на ")
                    : WorkspaceText.Choose("view clock moved to ", "часы вида переведены на ")) +
                FormattableString.Invariant($"JD {jump.After.ToJulianDate():F6} TDB.");
        }
        _deltaT.Select((int)session.DeltaT);
        UpdateNightMode(session.NightMode);
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls controls = _views[index];

            var epoch = session[index].Epoch;
            // Catalogue Lua callbacks can change internal state even at the same epoch.
            var snapshot = _addOns?.Decoded.AllowAddonLua != true && ReferenceEquals(controls.SnapshotWorld, session.World) &&
                controls.Snapshot is { } cached && cached.Epoch == epoch && cached.DeltaT == session.DeltaT
                    ? cached
                    : session.World.Graph.Evaluate(epoch, session.DeltaT);
            controls.SnapshotWorld = session.World;
            session.UpdateCamera(index, snapshot, elapsed);
            if (!controls.DraggingCamera && !controls.Looking)
                session.UpdateAutoOrbit(index, snapshot, elapsed);
            session.UpdateShip(index, snapshot, elapsed);
            ShipView? ship = session.Ship(index);
            ObserverView view = session[index];
            controls.Snapshot = snapshot;
            controls.Renderer.ShowPlanetOrbits = !view.Sky.HideOrbits;
            // Stateful add-on factories keep their original synchronous evaluation order.
            controls.Renderer.IncrementalPaths = _addOns?.Decoded.AllowAddonLua != true && !PathsRequireImmediate;
            controls.Renderer.ShowSurfaceSiteLabels = view.Sky.SurfacePlaces;
            controls.Renderer.SetLandscape(ViewLandscape(view), view.EarthObserver?.HideLandscapeFog != true);
            controls.Renderer.ArchivedGeometry = index == 0 ? (_replay as LegacyCameraSequence)?.CurrentGeometry : null;
            bool available = controls.Renderer.Draw(view, snapshot, session.DeltaT);
            // Scale only the 3D buffer. Logical coordinates, 2D overlays and the physical ray integrator stay intact.
            var viewport = (SubViewport)controls.Renderer.GetViewport();
            float renderScale = controls.Renderer.CompactLensVisible ? LensRenderScale : 1;
            if (viewport.Scaling3DScale != renderScale) viewport.Scaling3DScale = renderScale;
            UpdatePointerReadout(controls, view.Sky);
            controls.Renderer.DrawShip(ship, view.Navigation);
            controls.Renderer.SetScriptNotice(session.Notice(index));
            controls.Renderer.DrawScriptMarkers(view, snapshot, session.Markers(index), session.DeltaT);
            controls.Renderer.SetMissionAnnotations(_addOns?.Decoded.SampledMissions.Annotations ?? [], session[index].Epoch);
            controls.Renderer.DrawReferenceMarks(view, session.ReferenceGeometry(index, snapshot));
            controls.Measurements.Update(controls.Renderer, view, snapshot, session.World, session.DeltaT);
            controls.Epoch.Text = FormattableString.Invariant($"{WorkspaceText.Choose("View", "Вид")} {index + 1}   JD {view.Epoch.ToJulianDate():F6} TDB");
            if (view.LightTimeDelayed)
                controls.Epoch.Text += "   ·   " + WorkspaceText.Choose("Clock offset", "Сдвиг часов") +
                    (view.LightTimeOffsetNanoseconds is { } offset
                        ? FormattableString.Invariant($" −{offset / 1e9:G6} s") : " (legacy)");
            if (ship is not null)
            {
                string targetName = ship.Target is { } aim ? session.World.Catalogue.Find(aim)?.Name ?? aim.Value : "—";
                controls.Epoch.Text += FormattableString.Invariant($"   ·   Ship {ship.Pilot} · {ship.SpeedKilometersPerSecond:G4} km/s · {targetName}");
            }
            else if (session.IsFlying(index))
                controls.Epoch.Text += WorkspaceText.Choose("   ·   Flying to target", "   ·   Перелёт к цели");
            else if (view.Navigation is { Tracking: true, Target: { } target })
                controls.Epoch.Text += $"   ·   {WorkspaceText.Choose("Tracking", "Слежение:")} {session.World.Catalogue.Find(target)?.Name ?? target.Value}";
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
            controls.Play.Text = PlaybackButton(view.Playing);
            bool preset = view.Navigation is null;
            controls.Mode.Select(preset ? (int)view.Mode : NavigationOption);
            controls.Mode.SetItemDisabled((int)CameraMode.Surface, _surfaceTextures is null && view.EarthObserver?.IsEarth != false);
            controls.FieldOfView.Visible = !preset || view.Mode == CameraMode.Surface;
            _viewHost.SetFieldValue(controls, view.EffectiveFieldOfView);
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

    private static bool IsLoadError(Exception error)
    {
        // Out-of-range values usually come from the data itself, such as an epoch outside the supported calendar.
        if (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or System.Text.Json.JsonException or ArgumentOutOfRangeException)
            return true;
        if (error is not (OverflowException or KeyNotFoundException or InvalidOperationException or ArgumentException
            or NotSupportedException))
            return false;
        // These usually mean a defect rather than bad input. The user still gets the short message, and the log keeps
        // the stack so that the defect can be found.
        GD.PushError($"SpaceTangent: unexpected {error.GetType().Name} while loading: {error}");
        return true;
    }

    private static string SettingsPath()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH");
        return string.IsNullOrWhiteSpace(configured) ? ProjectSettings.GlobalizePath("user://delta-t.txt") : configured;
    }

    private static DeltaTModel ReadDeltaT() => DeltaTPreferenceFile.Load(SettingsPath());

    private static void SaveDeltaT(DeltaTModel model) => DeltaTPreferenceFile.Save(SettingsPath(), model);

    private static string PlaybackButton(bool playing) => ViewControls.PlaybackButton(playing);

    private static Button AddButton(Container parent, string text, Action onPressed) =>
        SimulatorButtons.Add(parent, text, onPressed);

}
