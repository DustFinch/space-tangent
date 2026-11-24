// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

internal sealed record ObserverViewActions(Action<int, double> Advance, Action<int> TogglePlaying,
    Action<int, CameraMode> SetMode, Action<int, double> SetFieldOfView, Action<int, int> SelectSatellite,
    Action<int, string> SeekTimeline, Action<int, LunarTerrainCamera> SetTerrainCamera,
    Action<int, LineEdit, string> SeekTerrain, Action<int, InputEvent> HandleInput, Action ViewSizeChanged);

// Owns controls, active-index remapping and divider subscriptions; application
// operations and render-resource binding cross explicit callbacks.
internal sealed class ObserverViewHost
{
    internal static readonly int NavigationOption = Enum.GetValues<CameraMode>().Length;
    internal List<ViewControls> Views { get; } = [];
    internal int Active { get; set; }
    private SimulationSession _session = null!;
    private BoxContainer _panels = null!;
    private ObserverViewActions _actions = null!;
    private Func<SimulationViewport> _createRenderer = null!;
    private Action<SimulationViewport> _bindRenderer = null!, _viewRemoved = null!;
    private Action<ViewControls> _bindAddons = null!;
    private StyleBoxFlat _activeFrame = null!, _inactiveFrame = null!;
    private bool _hasSurfaceTextures;
    private bool _refreshingField;

    internal void Initialize(SimulationSession session, BoxContainer panels, ObserverViewActions actions,
        Func<SimulationViewport> createRenderer, Action<SimulationViewport> bindRenderer,
        Action<ViewControls> bindAddons, Action<SimulationViewport> viewRemoved,
        StyleBoxFlat activeFrame, StyleBoxFlat inactiveFrame, bool hasSurfaceTextures)
    {
        _session = session; _panels = panels; _actions = actions;
        _createRenderer = createRenderer; _bindRenderer = bindRenderer;
        _bindAddons = bindAddons; _viewRemoved = viewRemoved;
        _activeFrame = activeFrame; _inactiveFrame = inactiveFrame;
        _hasSurfaceTextures = hasSurfaceTextures;
    }

    internal void Relayout()
    {
        _panels.Vertical = _session.SplitLayout == ViewSplit.Stacked;
        while (Views.Count < _session.ViewCount)
            AddView();
        while (Views.Count > _session.ViewCount)
            RemovePanel(Views.Count - 1);

        for (int index = 0; index < Views.Count; index++)
        {
            ViewControls view = Views[index];
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
        AttachView(_session.Layout, _panels, "");
    }

    private Control AttachView(ObserverLayout layout, Container parent, string path)
    {
        if (layout.View is { } index)
        {
            PanelContainer panel = Views[index].Panel;
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
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
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
            if (!GodotObject.IsInstanceValid(split) || !split.IsInsideTree())
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
            if (!GodotObject.IsInstanceValid(split) || !split.IsInsideTree())
                return;
            float total = Extent(first) + Extent(second);
            if (total <= 0)
                return;
            share = Extent(first) / total;
            _session.ResizeViewDivider(path, share);
        }
    }

    internal void AddView()
    {
        int index = Views.Count;
        var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        int CurrentIndex() => Views.FindIndex(view => ReferenceEquals(view.Panel, frame));
        _panels.AddChild(frame);
        var panel = new VBoxContainer();
        frame.AddChild(panel);
        var tools = new HFlowContainer();
        panel.AddChild(tools);
        var epoch = new Label { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        panel.AddChild(epoch);
        AddButton(tools, "−1 h", () => _actions.Advance(CurrentIndex(), -3600));
        Button play = AddButton(tools, ViewControls.PlaybackButton(false), () => _actions.TogglePlaying(CurrentIndex()));
        Button advanceHour = AddButton(tools, "+1 h", () => _actions.Advance(CurrentIndex(), 3600));
        advanceHour.Name = $"AdvanceHour{index}";
        var mode = new OptionButton();
        mode.AddItem(WorkspaceText.Choose("Inner planets", "Внутренние планеты"), (int)CameraMode.Solar);
        mode.AddItem(WorkspaceText.Choose("Earth–Moon view", "Земля и Луна"), (int)CameraMode.Lunar);
        mode.AddItem(WorkspaceText.Choose("Earth satellites", "Спутники Земли"), (int)CameraMode.Satellites);
        mode.AddItem(WorkspaceText.Choose("Surface", "Поверхность"), (int)CameraMode.Surface);
        mode.AddItem(WorkspaceText.Choose("Cassini at Saturn", "Cassini у Сатурна"), (int)CameraMode.Mission);
        mode.AddItem(WorkspaceText.Choose("Cassini timeline", "Полёт Cassini"), (int)CameraMode.Timeline);
        mode.AddItem("Huygens", (int)CameraMode.Probe);
        mode.AddItem(WorkspaceText.Choose("Lunar terrain", "Рельеф Луны"), (int)CameraMode.LunarTerrain);
        mode.AddItem(WorkspaceText.Choose("Outer planets", "Внешние планеты"), (int)CameraMode.SolarOuter);
        mode.AddItem(WorkspaceText.Choose("Olympus Mons", "Олимп"), (int)CameraMode.MarsTerrain);
        mode.AddItem(WorkspaceText.Choose("Object camera", "Камера объекта"), NavigationOption);
        mode.SetItemDisabled(NavigationOption, true);
        mode.SetItemDisabled((int)CameraMode.Surface, !_hasSurfaceTextures);
        mode.ItemSelected += item => _actions.SetMode(CurrentIndex(), (CameraMode)mode.GetItemId((int)item));
        tools.AddChild(mode);
        var field = new SpinBox { Name = $"ViewField{index}", MinValue = 2, MaxValue = 120, Step = .01,
            Value = _session[index].EffectiveFieldOfView, LocalizeNumeralSystem = false,
            CustomMinimumSize = new Vector2(112, 0),
            Suffix = "° FOV", TooltipText = "Vertical field of view" };
        field.ValueChanged += value =>
        {
            if (_refreshingField) return;
            _actions.SetFieldOfView(CurrentIndex(), value);
        };
        tools.AddChild(field);
        var selection = new OptionButton();
        selection.ItemSelected += item => _actions.SelectSatellite(CurrentIndex(), (int)item);
        tools.AddChild(selection);
        var timelineEpoch = new LineEdit { Name = "TimelineEpoch", PlaceholderText = "JD TDB ↵", Visible = false,
            TooltipText = "Enter a TDB Julian date, such as 2453167.5, and press Enter." };
        timelineEpoch.TextSubmitted += text => _actions.SeekTimeline(CurrentIndex(), text);
        panel.AddChild(timelineEpoch);

        var terrainTools = new HBoxContainer { Visible = false };
        AddButton(terrainTools, "Globe", () => _actions.SetTerrainCamera(CurrentIndex(), LunarTerrainCamera.Globe));
        AddButton(terrainTools, "Copernicus", () => _actions.SetTerrainCamera(CurrentIndex(), LunarTerrainCamera.Copernicus));
        var terrainCoordinates = new LineEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            PlaceholderText = "Lon°, lat°, width km ↵",
            TooltipText = "East longitude, north latitude and view width in km. Separate values with commas."
        };
        terrainCoordinates.TextSubmitted += text => _actions.SeekTerrain(CurrentIndex(), terrainCoordinates, text);
        terrainTools.AddChild(terrainCoordinates);
        panel.AddChild(terrainTools);

        var container = new SubViewportContainer
        {
            Name = $"ViewCanvas{index}",
            Stretch = true,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        container.GuiInput += input => _actions.HandleInput(CurrentIndex(), input);

        panel.AddChild(container);
        var viewport = new SubViewport { OwnWorld3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        container.AddChild(viewport);
        viewport.SizeChanged += _actions.ViewSizeChanged;
        var renderer = _createRenderer();
        viewport.AddChild(renderer);
        _bindRenderer(renderer);
        var measurements = new MeasurementOverlay { Name = "Measurements" };
        viewport.AddChild(measurements);
        var controls = new ViewControls(frame, renderer, epoch, play, mode, selection, timelineEpoch,
            terrainTools, terrainCoordinates, field, advanceHour, container, measurements);
        container.MouseExited += () =>
        {
            controls.PointerPosition = null;
            controls.PointerReadout?.Hide();
        };
        Views.Add(controls);
        _bindAddons(controls);
    }

    internal void RemovePanel(int index)
    {
        var controls = Views[index];
        PanelContainer panel = controls.Panel;
        _viewRemoved(controls.Renderer);
        Views.RemoveAt(index);
        panel.GetParent().RemoveChild(panel);
        panel.QueueFree();
    }

    internal void Close(int index)
    {
        _session.RemoveView(index);
        RemovePanel(index);
        if (Active > index)
            Active--;
        Active = Math.Min(Active, _session.ViewCount - 1);
    }

    internal void KeepActive()
    {
        for (int index = Views.Count - 1; index >= 0; index--)
        {
            if (index == Active)
                continue;
            _session.RemoveView(index);
            RemovePanel(index);
        }
        Active = 0;
    }

    internal void UpdateActiveStyle()
    {
        for (int index = 0; index < Views.Count; index++)
        {
            var frame = Views[index].Panel;
            var style = index == Active ? _activeFrame : _inactiveFrame;
            if (frame.GetThemeStylebox("panel") != style)
                frame.AddThemeStyleboxOverride("panel", style);
        }
    }

    internal void SetFieldValue(ViewControls controls, double value)
    {
        _refreshingField = true;
        try { controls.FieldOfView.Value = value; }
        finally { _refreshingField = false; }
    }

    private static Button AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += () => onPressed();
        parent.AddChild(button);
        return button;
    }
}
