// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Control
{
    private sealed record ViewControls(VBoxContainer Panel, SimulationViewport Renderer, Label Epoch,
        Button Play, OptionButton Mode, OptionButton Selection);
    private sealed record DecodedSatellite(ScienceSatellite Track, DecodedScienceModel Model);

    private readonly List<ViewControls> _views = [];
    private IReadOnlyList<PreparedSatellite> _assets = [];
    private SurfaceTextures? _surfaceTextures;
    private HBoxContainer _panels = null!;
    private Button _split = null!;
    private CheckButton _sync = null!;
    private Label _status = null!;
    private int _active;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCancellation;
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

        try
        {
            _status.Text = "Loading add-ons…";
            string root = LocalDataPaths.Find("SPACETANGENT_SCIENCE_ROOT", "science", directory: true);
            bool configured = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT"));
            var decoded = await Task.Run(() => DecodeAddOns(root, configured, token), token);
            if (!CanPublish(generation, token))
                return;
            _assets = PublishAddOns(decoded);
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

        Session = new(new SimulationWorld(ephemeris, _assets.Select(asset => asset.Track)),
            new(JulianInstant.J2000Day, 0, TimeScale.Tdb), ReadDeltaT());
        _split = AddButton(toolbar, "Split view", ToggleSplit);
        _sync = new CheckButton { Text = "Synchronize view time", ButtonPressed = true };
        _sync.Toggled += enabled => Synchronize(enabled, _active);
        toolbar.AddChild(_sync);
        var deltaT = new OptionButton();
        deltaT.AddItem("ΔT · Espenak–Meeus", (int)DeltaTModel.EspenakMeeus2006);
        deltaT.AddItem("ΔT · Morrison–Stephenson", (int)DeltaTModel.MorrisonStephenson2004);
        deltaT.AddItem("ΔT · Fixed offset", (int)DeltaTModel.FixedOffset);
        deltaT.Select((int)Session.DeltaT);
        deltaT.ItemSelected += index => SetDeltaT((DeltaTModel)deltaT.GetItemId((int)index));
        toolbar.AddChild(deltaT);
        AddButton(toolbar, "Reload add-ons", async () => await ReloadAddOns());
        if (_surfaceTextures is not null)
            AddButton(toolbar, "Full Moon", () => ShowFullMoon(_active));
        AddView();
        Refresh();
        InitializationFinished = true;
        GD.Print($"SpaceTangent world ready: DE440, {_assets.Count} science satellites, surface view {_surfaceTextures is not null}.");
        if (OS.GetCmdlineUserArgs().Contains("--smoke-startup"))
            GetTree().Quit();
    }

    public override void _ExitTree()
    {
        _loadGeneration++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    public override void _Process(double delta)
    {
        if (Session?.Tick(delta) == true)
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
        {
            Session.Split();
            AddView();
        }
        else
        {
            Session.CloseSecondView();
            var panel = _views[1].Panel;
            _views.RemoveAt(1);
            _panels.RemoveChild(panel);
            panel.QueueFree();
            _active = 0;
        }
        _split.Text = Session.ViewCount == 1 ? "Split view" : "Close second view";
        Refresh();
    }

    internal async Task<bool> ReloadAddOns()
    {
        CancellationToken token = BeginLoad();
        int generation = _loadGeneration;
        try
        {
            _status.Text = "Reloading add-ons…";
            string root = LocalDataPaths.Find("SPACETANGENT_SCIENCE_ROOT", "science", directory: true);
            bool configured = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT"));
            var decoded = await Task.Run(() => DecodeAddOns(root, configured, token), token);
            if (!CanPublish(generation, token))
                return false;
            IReadOnlyList<PreparedSatellite> next = PublishAddOns(decoded);
            var world = new SimulationWorld(Session!.World.Ephemeris, next.Select(asset => asset.Track));
            if (!Session.TryReplaceWorld(world))
                throw new InvalidOperationException("The new world does not cover the current epochs.");
            _assets = next;
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
        mode.SetItemDisabled((int)CameraMode.Surface, _surfaceTextures is null);
        mode.ItemSelected += item => SetMode(index, (CameraMode)mode.GetItemId((int)item));
        tools.AddChild(mode);
        var selection = new OptionButton();
        selection.ItemSelected += item => SelectSatellite(index, _assets[(int)item].Track.Id);
        tools.AddChild(selection);

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
        var controls = new ViewControls(panel, renderer, epoch, play, mode, selection);
        _views.Add(controls);
        ReplaceAssets(controls);
    }

    private void ReplaceAssets(ViewControls view)
    {
        view.Renderer.ReplaceSatellites(_assets);
        view.Selection.Clear();
        foreach (PreparedSatellite asset in _assets)
            view.Selection.AddItem(asset.Track.Name);
        view.Mode.SetItemDisabled((int)CameraMode.Satellites, _assets.Count == 0);
    }

    private void Refresh()
    {
        SimulationSession session = Session!;
        for (int index = 0; index < _views.Count; index++)
        {
            ViewControls controls = _views[index];
            ObserverView view = session[index];
            bool available = controls.Renderer.Draw(view, session.World.Graph.Evaluate(view.Epoch), session.DeltaT);
            controls.Epoch.Text = FormattableString.Invariant($"View {index + 1}   JD {view.Epoch.ToJulianDate():F6} TDB");
            if (!available)
                controls.Epoch.Text += "   Surface view is available for 1900–2099.";
            controls.Play.Text = view.Playing ? "Pause" : "Play";
            controls.Mode.Select((int)view.Mode);
            controls.Selection.Visible = view.Mode == CameraMode.Satellites;
            int selected = -1;
            for (int item = 0; item < _assets.Count && selected < 0; item++)
            {
                if (_assets[item].Track.Id == view.Selection)
                    selected = item;
            }
            controls.Selection.Select(selected);
        }
    }

    private static IReadOnlyList<DecodedSatellite> DecodeAddOns(string root, bool configured, CancellationToken cancellation)
    {
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        if (!configured && !File.Exists(catalog))
            return [];

        var satellites = new List<DecodedSatellite>();
        foreach (ScienceSatellite track in ScienceSatelliteCatalog.Load(catalog, root))
        {
            cancellation.ThrowIfCancellationRequested();
            satellites.Add(new(track, ScienceModelDecoder.Decode(track.MeshPath, cancellation)));
        }
        return satellites;
    }

    private static IReadOnlyList<PreparedSatellite> PublishAddOns(IReadOnlyList<DecodedSatellite> decoded)
    {
        var satellites = new List<PreparedSatellite>(decoded.Count);
        foreach (DecodedSatellite item in decoded)
            satellites.Add(new(item.Track, ScienceMeshLoader.Publish(item.Model), (float)(18 / item.Model.Diameter)));
        return satellites;
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
