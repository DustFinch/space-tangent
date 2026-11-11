// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using System.Globalization;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Control
{
    private sealed record ViewControls(VBoxContainer Panel, SimulationViewport Renderer, Label Epoch,
        Button Play, OptionButton Mode, OptionButton Selection, LineEdit TimelineEpoch, HBoxContainer TerrainTools,
        LineEdit TerrainCoordinates);

    private readonly List<ViewControls> _views = [];
    private AddonGeneration? _addOns;
    private IReadOnlyList<PreparedSatellite> Assets => _addOns?.Satellites ?? [];
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
    internal AddonDiagnostic? LastAddonDiagnostic { get; private set; }

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
            var decoded = await AddonLoader.Load(ephemeris, null, token);
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
        GD.Print($"SpaceTangent world ready: DE440, {Assets.Count} science satellites, surface view {_surfaceTextures is not null}.");
        if (world.MissionName is { } mission)
            GD.Print($"SpaceTangent mission ready: {mission}.");
        if (world.TimelineId is not null)
            GD.Print("SpaceTangent Cassini timeline ready.");
        if (world.Probe is not null)
            GD.Print("SpaceTangent Huygens ready.");
        if (world.HasLunarTerrain)
            GD.Print("SpaceTangent lunar terrain ready.");
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
            AddonGeneration? previous = _addOns;
            var decoded = await AddonLoader.Load(Session!.World.Ephemeris, previous?.Decoded, token);
            if (!CanPublish(generation, token))
                return false;
            AddonGeneration next = AddonDiagnostics.Read(AddonStage.RenderResources, null, () => AddonLoader.Publish(decoded, previous));
            if (!ReferenceEquals(previous, next))
            {
                if (!Session.TryReplaceWorld(decoded.World))
                    throw new InvalidOperationException("The new world does not cover the current epochs.");
                _addOns = next;
                foreach (ViewControls view in _views)
                    ReplaceAssets(view);
                Refresh();
            }
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
        mode.AddItem("Huygens", (int)CameraMode.Probe);
        mode.AddItem("Lunar terrain", (int)CameraMode.LunarTerrain);
        mode.SetItemDisabled((int)CameraMode.Surface, _surfaceTextures is null);
        mode.ItemSelected += item => SetMode(index, (CameraMode)mode.GetItemId((int)item));
        tools.AddChild(mode);
        var selection = new OptionButton();
        selection.ItemSelected += item => SelectSatellite(index, Assets[(int)item].Track.Id);
        tools.AddChild(selection);
        var timelineEpoch = new LineEdit { Name = "TimelineEpoch", PlaceholderText = "JD TDB ↵", Visible = false,
            TooltipText = "Enter a TDB Julian date, such as 2453167.5, and press Enter." };
        timelineEpoch.TextSubmitted += text => SeekTimeline(index, text);
        panel.AddChild(timelineEpoch);

        var terrainTools = new HBoxContainer { Visible = false };
        AddButton(terrainTools, "Globe", () => SetLunarTerrainCamera(index, LunarTerrainCamera.Globe));
        AddButton(terrainTools, "Copernicus", () => SetLunarTerrainCamera(index, LunarTerrainCamera.Copernicus));
        var terrainCoordinates = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "Lon°, lat°, width km ↵",
            TooltipText = "East longitude, north latitude and view width in km. Separate values with commas."
        };
        terrainCoordinates.TextSubmitted += text => SeekTerrain(index, terrainCoordinates, text);
        terrainTools.AddChild(terrainCoordinates);
        panel.AddChild(terrainTools);

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
        var controls = new ViewControls(panel, renderer, epoch, play, mode, selection, timelineEpoch,
            terrainTools, terrainCoordinates);
        _views.Add(controls);
        ReplaceAssets(controls);
    }

    private void ReplaceAssets(ViewControls view)
    {
        view.Renderer.ReplaceSatellites(Assets);
        view.Renderer.ReplaceMission(_addOns?.Mission, _addOns?.Timeline, _addOns?.Probe);
        view.Renderer.ReplaceLunarTerrain(_addOns?.Terrain);
        view.Selection.Clear();
        foreach (PreparedSatellite asset in Assets)
            view.Selection.AddItem(asset.Track.Name);
        view.Mode.SetItemDisabled((int)CameraMode.Satellites, Assets.Count == 0);
        view.Mode.SetItemDisabled((int)CameraMode.Mission, _addOns?.Mission is null);
        view.Mode.SetItemDisabled((int)CameraMode.Timeline, _addOns?.Timeline is null);
        view.Mode.SetItemDisabled((int)CameraMode.Probe, _addOns?.Probe is null);
        view.Mode.SetItemDisabled((int)CameraMode.LunarTerrain, _addOns?.Terrain is null);
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
                    CameraMode.Probe => "   Huygens is outside the available timeline or kernel coverage.",
                    CameraMode.LunarTerrain => "   Lunar terrain is unavailable.",
                    _ => "   Surface view is available for 1900–2099."
                };
            if (controls.Renderer.ProbeStatus is { } phase)
                controls.Epoch.Text += "\n" + phase;
            controls.Play.Text = view.Playing ? "Pause" : "Play";
            controls.Mode.Select((int)view.Mode);
            controls.Selection.Visible = view.Mode == CameraMode.Satellites;
            controls.TimelineEpoch.Visible = view.Mode is CameraMode.Timeline or CameraMode.Probe;
            controls.TerrainTools.Visible = view.Mode == CameraMode.LunarTerrain;
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
