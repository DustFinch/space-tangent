// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Globalization;
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Control
{
    private sealed record ViewControls(VBoxContainer Panel, SimulationViewport Renderer, Label Epoch,
        Button Play, OptionButton Mode, OptionButton Selection, LineEdit TimelineEpoch);
    private sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);
    private sealed record DecodedMission(DecodedScienceModel Model, IReadOnlyList<SpiceSensor> Sensors, SaturnSurface? Surface);
    private sealed record DecodedAddOns(SimulationWorld World, List<DecodedSatellite> Satellites, DecodedMission? Mission,
        DecodedScienceModel? Timeline);
    private sealed record PublishedAddOns(IReadOnlyList<PreparedSatellite> Satellites, PreparedMission? Mission, PreparedMission? Timeline);

    private readonly List<ViewControls> _views = [];
    private IReadOnlyList<PreparedSatellite> _assets = [];
    private PreparedMission? _mission;
    private PreparedMission? _timeline;
    private SurfaceTextures? _surfaceTextures;
    private HBoxContainer _panels = null!;
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

    public override async void _Ready()
    {
        var layout = new VBoxContainer();
        AddChild(layout);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var toolbar = new HBoxContainer();
        layout.AddChild(toolbar);
        _status = new Label();
        layout.AddChild(_status);
        _panels = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        layout.AddChild(_panels);
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

        SimulationWorld world = new(ephemeris, []);
        try
        {
            _status.Text = "Loading add-ons…";
            DecodedAddOns decoded = await LoadAddOns(ephemeris, token);
            if (!CanPublish(generation, token))
                return;
            PublishedAddOns assets = PublishAddOns(decoded);
            world = decoded.World;
            _assets = assets.Satellites;
            _mission = assets.Mission;
            _timeline = assets.Timeline;
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
            _status.Text = error.Message;
        }
        try
        {
            _surfaceTextures = SurfaceProjection.LoadTextures();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = error.Message;
        }

        Session = new(world,
            new(JulianInstant.J2000Day, 0, TimeScale.Tdb), ReadDeltaT());
        _split = AddButton(toolbar, "Split view", ToggleSplit);
        _sync = new CheckButton { Text = "Synchronize view time", ButtonPressed = true };
        _sync.Toggled += enabled => Synchronize(enabled, _active);
        toolbar.AddChild(_sync);
        _deltaT = new OptionButton();
        _deltaT.AddItem("ΔT · Espenak–Meeus", (int)DeltaTModel.EspenakMeeus2006);
        _deltaT.AddItem("ΔT · Morrison–Stephenson", (int)DeltaTModel.MorrisonStephenson2004);
        _deltaT.AddItem("ΔT · Fixed offset", (int)DeltaTModel.FixedOffset);
        _deltaT.ItemSelected += index => SetDeltaT((DeltaTModel)_deltaT.GetItemId((int)index));
        toolbar.AddChild(_deltaT);
        AddScenarioMenu(toolbar);
        AddScriptMenu(toolbar);
        AddButton(toolbar, "Reload add-ons", async () => await ReloadAddOns());
        if (_surfaceTextures is not null)
            AddButton(toolbar, "Full Moon", () => ShowFullMoon(_active));
        AddView();
        Refresh();
        InitializationFinished = true;
        GD.Print($"SpaceTangent world ready: DE440, {_assets.Count} science satellites, surface view {_surfaceTextures is not null}.");
        if (world.MissionName is { } mission)
            GD.Print($"SpaceTangent mission ready: {mission}.");
        if (world.TimelineId is not null)
            GD.Print("SpaceTangent Cassini timeline ready.");
        if (OS.GetCmdlineUserArgs().Contains("--smoke-startup"))
            GetTree().Quit();
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
        if (changed)
            Refresh();
    }

    internal SimulationViewport Renderer(int view) => _views[view].Renderer;

    internal void SetMode(int view, CameraMode mode)
    {
        _active = view;
        Session!.SetMode(view, mode);
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
            Session.Split();
        else
        {
            Session.CloseSecondView();
            if (_script?.View == 1)
                StopScript();
            _active = 0;
        }
        UpdateViewLayout();
        Refresh();
    }

    private void UpdateViewLayout()
    {
        if (_views.Count < Session!.ViewCount)
            AddView();
        else if (_views.Count > Session.ViewCount)
        {
            var panel = _views[1].Panel;
            _views.RemoveAt(1);
            _panels.RemoveChild(panel);
            panel.QueueFree();
        }
        _split.Text = Session.ViewCount == 1 ? "Split view" : "Close second view";
    }

    private void AddScenarioMenu(Container toolbar)
    {
        var menu = new MenuButton { Text = "Scenario" };
        toolbar.AddChild(menu);
        var open = new FileDialog { Name = "OpenScenario", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        var save = new FileDialog { Name = "SaveScenario", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile, DeletingEnabled = false };
        AddChild(open);
        AddChild(save);
        open.AddFilter("*.stscene", "SpaceTangent scenarios");
        save.AddFilter("*.stscene", "SpaceTangent scenarios");
        open.FileSelected += path => LoadScenario(path);
        save.FileSelected += path => SaveScenario(path);
        PopupMenu popup = menu.GetPopup();
        popup.AddItem("Open scenario…", 0);
        popup.AddItem("Save scenario…", 1);
        popup.IdPressed += id => (id == 0 ? open : save).PopupCentered(new Vector2I(700, 460));
    }

    private void AddScriptMenu(Container toolbar)
    {
        var menu = new MenuButton { Text = "Script" };
        toolbar.AddChild(menu);
        var open = new FileDialog { Name = "OpenScript", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        AddChild(open);
        open.AddFilter("*.lua,*.celx", "Lua scripts");
        open.FileSelected += path => RunScript(path);
        _scriptMenu = menu.GetPopup();
        _scriptMenu.AddItem("Run Lua…", 0);
        _scriptMenu.AddItem("Stop script", 1);
        _scriptMenu.SetItemDisabled(1, true);
        _scriptMenu.IdPressed += id =>
        {
            if (id == 0)
                open.PopupCentered(new Vector2I(700, 460));
            else
                StopScript();
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
        _script?.Stop();
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
            SimulationScenario scenario = ScenarioFile.Load(path);
            if (_surfaceTextures is null && scenario.Views.Any(view => view.Mode == CameraMode.Surface))
                throw new InvalidDataException("The scenario needs the Earth surface textures.");
            Session!.RestoreScenario(scenario);
            if (_script is not null)
                StopScript();
            _active = scenario.ActiveView;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open scenario. {error.Message}";
            return false;
        }
        UpdateViewLayout();
        _sync.SetPressedNoSignal(Session.Synchronized);
        Refresh();
        _status.Text = "Scenario opened.";
        return true;
    }

    internal async Task<bool> ReloadAddOns()
    {
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            _status.Text = "Reloading add-ons…";
            DecodedAddOns decoded = await LoadAddOns(Session!.World.Ephemeris, token);
            if (!CanPublish(generation, token))
                return false;
            PublishedAddOns next = PublishAddOns(decoded);
            if (!Session.TryReplaceWorld(decoded.World))
                throw new InvalidOperationException("The new world does not cover the current epochs.");
            _assets = next.Satellites;
            _mission = next.Mission;
            _timeline = next.Timeline;
            foreach (ViewControls view in _views)
                ReplaceAssets(view);
            Refresh();
            _status.Text = "Add-ons reloaded.";
            GD.Print($"SpaceTangent add-ons reloaded: {_assets.Count} science satellites.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            if (CanPublish(generation, token))
                _status.Text = $"Could not reload add-ons. {error.Message}";
            return false;
        }
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
        var panel = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _panels.AddChild(panel);
        var tools = new HBoxContainer();
        panel.AddChild(tools);
        var epoch = new Label();
        panel.AddChild(epoch);
        AddButton(tools, "−1 h", () => Advance(index, -3600));
        Button play = AddButton(tools, "Play", () =>
        {
            _active = index;
            Session!.TogglePlaying(index);
            Refresh();
        });
        AddButton(tools, "+1 h", () => Advance(index, 3600));
        var mode = new OptionButton();
        mode.AddItem("Solar overview", (int)CameraMode.Solar);
        mode.AddItem("Earth–Moon view", (int)CameraMode.Lunar);
        mode.AddItem("Earth satellites", (int)CameraMode.Satellites);
        mode.AddItem("Earth surface", (int)CameraMode.Surface);
        mode.AddItem("Cassini at Saturn", (int)CameraMode.Mission);
        mode.AddItem("Cassini timeline", (int)CameraMode.Timeline);
        mode.SetItemDisabled((int)CameraMode.Surface, _surfaceTextures is null);
        mode.ItemSelected += item => SetMode(index, (CameraMode)mode.GetItemId((int)item));
        tools.AddChild(mode);
        var selection = new OptionButton();
        selection.ItemSelected += item => SelectSatellite(index, _assets[(int)item].Track.Id);
        tools.AddChild(selection);
        var timelineEpoch = new LineEdit { Name = "TimelineEpoch", PlaceholderText = "JD TDB ↵", Visible = false,
            TooltipText = "Enter a TDB Julian date, such as 2453167.5, and press Enter." };
        timelineEpoch.TextSubmitted += text => SeekTimeline(index, text);
        panel.AddChild(timelineEpoch);

        var container = new SubViewportContainer
        {
            Stretch = true,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        container.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true })
                _active = index;
        };
        panel.AddChild(container);
        var viewport = new SubViewport { OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        container.AddChild(viewport);
        var renderer = new SimulationViewport();
        viewport.AddChild(renderer);
        if (_surfaceTextures is { } textures)
            renderer.SetSurfaceTextures(textures);
        var controls = new ViewControls(panel, renderer, epoch, play, mode, selection, timelineEpoch);
        _views.Add(controls);
        ReplaceAssets(controls);
    }

    private void ReplaceAssets(ViewControls view)
    {
        view.Renderer.ReplaceSatellites(_assets);
        view.Renderer.ReplaceMission(_mission, _timeline);
        view.Selection.Clear();
        foreach (PreparedSatellite asset in _assets)
            view.Selection.AddItem(asset.Track.Name);
        view.Mode.SetItemDisabled((int)CameraMode.Satellites, _assets.Count == 0);
        view.Mode.SetItemDisabled((int)CameraMode.Mission, _mission is null);
        view.Mode.SetItemDisabled((int)CameraMode.Timeline, _timeline is null);
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

    private void Refresh()
    {
        SimulationSession session = Session!;
        _deltaT.Select((int)session.DeltaT);
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls controls = _views[index];
            ObserverView view = session[index];
            bool available = controls.Renderer.Draw(view, session.World.Graph.Evaluate(view.Epoch), session.DeltaT);
            controls.Epoch.Text = FormattableString.Invariant($"View {index + 1}   JD {view.Epoch.ToJulianDate():F6} TDB");
            if (!available)
                controls.Epoch.Text += view.Mode switch
                {
                    CameraMode.Mission => "   Cassini state or pointing is outside kernel coverage.",
                    CameraMode.Timeline => "   Cassini is outside the sampled timeline.",
                    _ => "   Surface view is available for 1900–2099."
                };
            controls.Play.Text = view.Playing ? "Pause" : "Play";
            controls.Mode.Select((int)view.Mode);
            controls.Selection.Visible = view.Mode == CameraMode.Satellites;
            controls.TimelineEpoch.Visible = view.Mode == CameraMode.Timeline;
            int selected = -1;
            for (int item = 0; item < _assets.Count && selected < 0; item++)
            {
                if (_assets[item].Track.Id == view.Selection)
                    selected = item;
            }
            controls.Selection.Select(selected);
        }
    }

    private static Task<DecodedAddOns> LoadAddOns(De440Ephemeris ephemeris, CancellationToken cancellation)
    {
        string root = LocalDataPaths.Find("SPACETANGENT_SCIENCE_ROOT", "science", directory: true);
        bool configured = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT"));
        string? explicitMission = System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_CATALOG");
        string missionJson = LocalDataPaths.Find("SPACETANGENT_MISSION_CATALOG", "mission/cassini.json");
        string missionSsc = LocalDataPaths.Find("SPACETANGENT_MISSION_CATALOG", "mission/cassini.ssc");
        const string TimelineEnvironment = "SPACETANGENT_TIMELINE_ROOT";
        string timelineRoot = LocalDataPaths.Find(TimelineEnvironment, "timeline", directory: true);
        bool timelineConfigured = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(TimelineEnvironment));
        return Task.Run(() => DecodeAddOns(ephemeris, root, configured, explicitMission, missionJson, missionSsc,
            timelineRoot, timelineConfigured, cancellation),
            cancellation);
    }

    private static DecodedAddOns DecodeAddOns(De440Ephemeris ephemeris, string root, bool configured,
        string? explicitMission, string missionJson, string missionSsc, string timelineRoot, bool timelineConfigured,
        CancellationToken cancellation)
    {
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        var satellites = new List<DecodedSatellite>();
        if (configured || File.Exists(catalog))
        {
            foreach (ScienceSatellite track in ScienceSatelliteCatalog.Load(catalog, root))
            {
                cancellation.ThrowIfCancellationRequested();
                satellites.Add(new(track, ScienceModelDecoder.Decode(track.MeshPath, cancellation)));
            }
        }
        cancellation.ThrowIfCancellationRequested();
        string? missionPath = !string.IsNullOrWhiteSpace(explicitMission) ? explicitMission
            : File.Exists(missionJson) ? missionJson : File.Exists(missionSsc) ? missionSsc : null;
        SpiceSpacecraft? mission = null;
        DecodedMission? geometry = null;
        if (missionPath is not null)
        {
            var (track, sensors) = ReadMission(missionPath);
            mission = track;
            string directory = Path.GetDirectoryName(Path.GetFullPath(missionPath))!;
            string centers = Path.Combine(directory, "sat480.bsp"), pck = Path.Combine(directory, "pck00011.tpc");
            SaturnSurface? surface = File.Exists(centers) && File.Exists(pck) ? new(centers, pck) : null;
            geometry = new(StudioMeshDecoder.Decode(track.MeshPath, cancellation), sensors, surface);
        }
        string timelineCatalog = Path.Combine(timelineRoot, "ssc", "cassini.ssc");
        SscSampledSpacecraft? timeline = null;
        DecodedScienceModel? timelineGeometry = null;
        if (timelineConfigured || File.Exists(timelineCatalog))
        {
            timeline = SscSampledCatalog.Load(timelineCatalog, timelineRoot, "Sol/Saturn/Cassini", cancellation);
            timelineGeometry = StudioMeshDecoder.Decode(timeline.MeshPath, cancellation);
        }
        return new(new SimulationWorld(ephemeris, satellites.Select(asset => asset.Track), mission, timeline),
            satellites, geometry, timelineGeometry);
    }

    private static (SpiceSpacecraft Track, IReadOnlyList<SpiceSensor> Sensors) ReadMission(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetDirectoryName(fullPath)!;
        SpiceCatalogResult catalog = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".ssc" => new(SscSpiceCatalog.Load(fullPath, root), []),
            ".json" => SpiceJsonCatalog.Load(fullPath, root),
            _ => throw new NotSupportedException("Use an SSC or JSON mission catalogue.")
        };
        IReadOnlyList<SpiceSpacecraft> entries = catalog.Spacecraft;
        if (entries.Count != 1 || entries[0].Target != -82 || entries[0].Center != 6 || entries[0].AttitudeFrame is null)
            throw new NotSupportedException("This mission view expects a single Cassini track at Saturn with CK pointing.");
        SpiceSpacecraft mission = entries[0];
        if (!mission.TrySample(SimulationSession.MissionPreview, out _) || !mission.TryAttitude(SimulationSession.MissionPreview, out _))
            throw new InvalidDataException("The mission kernels do not cover the Cassini orbit insertion preview.");
        return (mission, catalog.Sensors);
    }

    private static PublishedAddOns PublishAddOns(DecodedAddOns decoded)
    {
        var satellites = new List<PreparedSatellite>(decoded.Satellites.Count);
        foreach (DecodedSatellite item in decoded.Satellites)
            satellites.Add(new(item.Track, ScienceMeshLoader.Publish(item.Model), (float)(18 / item.Model.Diameter)));
        PreparedMission? mission = decoded.Mission is { } geometry
            ? new(decoded.World.MissionId!.Value, ScienceMeshLoader.Publish(geometry.Model),
                (float)(18 / geometry.Model.Diameter), SimulationViewport.CreateFrusta(geometry.Sensors),
                geometry.Surface, geometry.Sensors) : null;
        PreparedMission? timeline = decoded.Timeline is { } model
            ? new(decoded.World.TimelineId!.Value, ScienceMeshLoader.Publish(model), (float)(18 / model.Diameter), null, null, [])
            : null;
        return new(satellites, mission, timeline);
    }

    private static bool IsLoadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or OverflowException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or NotSupportedException;

    private static string SettingsPath() => System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH")
        ?? ProjectSettings.GlobalizePath("user://delta-t.txt");

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
