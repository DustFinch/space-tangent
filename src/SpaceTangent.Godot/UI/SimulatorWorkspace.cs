// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.UI;

internal sealed class SimulatorWorkspace
{
    private readonly record struct Page(Button Button, VBoxContainer Content, string Title);

    private readonly Dictionary<string, Page> _pages = new(StringComparer.Ordinal);
    private readonly VBoxContainer _rail;
    private readonly PanelContainer _sidebar;
    private readonly VBoxContainer _sidebarContent;
    private readonly Label _title, _shortcut;
    private string? _selectedPage;
    private readonly PanelContainer _card;
    private readonly Label _objectName, _objectKind;
    private readonly Label _objectDetails;
    private ObjectId? _shownObject;
    private readonly Button _goTo, _track;

    internal HBoxContainer Toolbar { get; }
    internal HBoxContainer Views { get; }
    internal Label Status { get; }
    internal Label Observation { get; }

    internal SimulatorWorkspace(Control root, Action goTo, Action track)
    {
        root.Theme = SimulatorTheme.Create();
        var layout = new HBoxContainer { Name = "Workspace" };
        layout.AddThemeConstantOverride("separation", 0);
        root.AddChild(layout);
        layout.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var rail = new PanelContainer { Name = "Navigation", CustomMinimumSize = new(44, 0) };
        layout.AddChild(rail);
        _rail = new VBoxContainer();
        _rail.AddThemeConstantOverride("separation", 2);
        rail.AddChild(_rail);
        var logo = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://UI/space-tangent.png"),
            CustomMinimumSize = new(34, 38),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TooltipText = "SpaceTangent"
        };
        _rail.AddChild(logo);
        _rail.AddChild(new Control { CustomMinimumSize = new(0, 5), MouseFilter = Control.MouseFilterEnum.Ignore });

        _sidebar = new PanelContainer { Name = "Sidebar", CustomMinimumSize = new(300, 0), Visible = false };
        _sidebar.AddThemeStyleboxOverride("panel", SimulatorTheme.Box(SimulatorTheme.Rail, SimulatorTheme.Border, 14, 12));
        layout.AddChild(_sidebar);
        _sidebarContent = new VBoxContainer();
        _sidebarContent.AddThemeConstantOverride("separation", 10);
        _sidebar.AddChild(_sidebarContent);
        var heading = new HBoxContainer();
        _sidebarContent.AddChild(heading);
        _title = new Label { ThemeTypeVariation = "WorkspaceTitle", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        heading.AddChild(_title);
        _shortcut = SimulatorTheme.Detail("");
        _shortcut.AutowrapMode = TextServer.AutowrapMode.Off;
        _shortcut.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        heading.AddChild(_shortcut);
        var close = new Button { Text = "×", TooltipText = "Close panel", FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => Open(null);
        heading.AddChild(close);
        _sidebarContent.AddChild(new HSeparator());

        var stage = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        stage.AddThemeStyleboxOverride("panel", SimulatorTheme.Box(SimulatorTheme.Ground, SimulatorTheme.Ground, 8, 8));
        layout.AddChild(stage);
        var stageContent = new VBoxContainer();
        stage.AddChild(stageContent);
        var header = new HBoxContainer();
        stageContent.AddChild(header);
        var modePanel = new PanelContainer();
        modePanel.AddThemeStyleboxOverride("panel", SimulatorTheme.Box(SimulatorTheme.Rail, SimulatorTheme.Border, 5, 4));
        header.AddChild(modePanel);
        Toolbar = new HBoxContainer { Name = "Toolbar" };
        Toolbar.AddThemeConstantOverride("separation", 0);
        modePanel.AddChild(Toolbar);
        header.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        var readout = new PanelContainer();
        readout.AddThemeStyleboxOverride("panel", SimulatorTheme.Box(SimulatorTheme.Rail, SimulatorTheme.Border, 9, 4));
        header.AddChild(readout);
        Observation = SimulatorTheme.Detail("");
        Observation.AutowrapMode = TextServer.AutowrapMode.Off;
        Observation.VerticalAlignment = VerticalAlignment.Center;
        readout.AddChild(Observation);
        Views = new HBoxContainer { Name = "Views", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        stageContent.AddChild(Views);
        Status = SimulatorTheme.Detail("");
        Status.Name = "WorkspaceStatus";
        stageContent.AddChild(Status);

        _card = new PanelContainer { Name = "ObjectCard", CustomMinimumSize = new(300, 0), Visible = false };
        _card.AddThemeStyleboxOverride("panel", SimulatorTheme.Box(SimulatorTheme.Rail, SimulatorTheme.Border, 14, 12));
        layout.AddChild(_card);
        var facts = new VBoxContainer();
        _card.AddChild(facts);
        var cardHeader = new HBoxContainer();
        facts.AddChild(cardHeader);
        _objectKind = SimulatorTheme.Section("");
        _objectKind.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        cardHeader.AddChild(_objectKind);
        var closeCard = new Button { Name = "CloseObjectCard", Text = "×", TooltipText = "Close object card",
            FocusMode = Control.FocusModeEnum.None };
        closeCard.Pressed += _card.Hide;
        cardHeader.AddChild(closeCard);
        _objectName = new Label { ThemeTypeVariation = "WorkspaceTitle", ClipText = true };
        facts.AddChild(_objectName);
        var actions = new HBoxContainer();
        facts.AddChild(actions);
        _goTo = CameraButton(actions, "GoToObject", "Go to", "Fly to the selected object · Esc cancels the flight", goTo);
        _track = CameraButton(actions, "TrackObject", "Track", "Keep the selected object centered as time advances", track);
        facts.AddChild(new HSeparator());
        facts.AddChild(SimulatorTheme.Section("Current state"));
        _objectDetails = SimulatorTheme.Detail("");
        facts.AddChild(_objectDetails);
    }

    private static Button CameraButton(Container parent, string name, string title, string tooltip, Action pressed)
    {
        var button = new Button { Name = name, Text = title, Disabled = true, TooltipText = tooltip,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    internal void ShowObject(CatalogueObject? target, string details, bool tracking, bool flying, bool star, bool surfaceAvailable)
    {
        if (_shownObject != target?.Id)
        {
            _shownObject = target?.Id;
            _card.Visible = target is not null;
        }
        _goTo.Disabled = star ? !surfaceAvailable : target is null && !flying;
        _track.Visible = !star;
        _track.Disabled = target is null || star;
        _goTo.Text = star ? "Point at" : flying ? "Stop flight" : "Go to";
        _goTo.TooltipText = star ? "Point the Earth observer at this star" : "Fly to the selected object · Esc cancels the flight";
        _track.Text = tracking ? "Stop tracking" : "Track";
        if (target is null)
            return;
        _objectKind.Text = target.Kind == CatalogueKind.DwarfPlanet ? "DWARF PLANET" : target.Kind.ToString().ToUpperInvariant();
        _objectName.Text = target.Name;
        _objectDetails.Text = details;
    }

    internal void RevealObjectCard()
    {
        if (_shownObject is not null)
            _card.Show();
    }

    internal VBoxContainer AddPage(string id, string title)
    {
        var button = new Button
        {
            Name = id + "Tab", TooltipText = title, ToggleMode = true,
            ThemeTypeVariation = "RailButton",
            CustomMinimumSize = new(30, 30), FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        var icon = new WorkspaceGlyph { Page = id, MouseFilter = Control.MouseFilterEnum.Ignore };
        button.AddChild(icon);
        icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.Pressed += () => Open(_selectedPage == id ? null : id);
        _rail.AddChild(button);
        var content = new VBoxContainer { Name = id + "Page" };
        content.AddThemeConstantOverride("separation", 10);
        var scroll = new ScrollContainer
        {
            Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(content);
        _sidebarContent.AddChild(scroll);
        _pages.Add(id, new(button, content, title));
        return content;
    }

    internal void AddRailSeparator() => _rail.AddChild(new HSeparator());

    internal void AddRailSpacer() =>
        _rail.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

    internal void Open(string? id)
    {
        _selectedPage = id;
        foreach (var (key, page) in _pages)
        {
            bool selected = key == id;
            ((Control)page.Content.GetParent()).Visible = selected;
            page.Button.SetPressedNoSignal(selected);
            if (selected)
                _title.Text = page.Title;
        }
        _shortcut.Text = id switch { "Search" => "Enter", "Time" => "Space", _ => "" };
        _shortcut.Visible = _shortcut.Text.Length != 0;
        _sidebar.Visible = id is not null;
    }
}
