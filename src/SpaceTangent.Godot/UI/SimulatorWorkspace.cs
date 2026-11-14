// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.UI;

internal sealed class SimulatorWorkspace
{
    private readonly record struct Page(Button Button, VBoxContainer Content, string Title, string Group, Button? Overview);

    private readonly Dictionary<string, Page> _pages = new(StringComparer.Ordinal);
    private readonly VBoxContainer _rail;
    private readonly PanelContainer _sidebar;
    private readonly VBoxContainer _sidebarContent;
    private readonly Label _title, _shortcut;
    private readonly HFlowContainer _sectionTabs;
    private string? _selectedPage;
    private readonly PanelContainer _card;
    private readonly Label _objectName, _objectKind;
    private readonly Label _objectDetails;
    private ObjectId? _shownObject;
    private readonly Button _goTo, _track, _detailsToggle;
    private bool _detailsExpanded;

    internal HBoxContainer Toolbar { get; }
    internal BoxContainer Views { get; }
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
        var close = new Button { Text = "×", TooltipText = WorkspaceText.Choose("Close panel", "Закрыть панель"), FocusMode = Control.FocusModeEnum.None };
        close.Pressed += () => Open(null);
        heading.AddChild(close);
        _sectionTabs = new HFlowContainer { Name = "SidebarSections" };
        _sidebarContent.AddChild(_sectionTabs);
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
        _detailsToggle = new Button
        {
            Name = "ObjectDetailsToggle", ToggleMode = true, Visible = false,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new(90, 0)
        };
        _detailsToggle.Toggled += expanded =>
        {
            _detailsExpanded = expanded;
            UpdateObjectCard();
        };
        header.AddChild(_detailsToggle);
        Views = new BoxContainer { Name = "Views", SizeFlagsVertical = Control.SizeFlags.ExpandFill };
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
        var closeCard = new Button { Name = "CloseObjectCard", Text = "×", TooltipText = WorkspaceText.Choose("Collapse object details", "Свернуть сведения об объекте"),
            FocusMode = Control.FocusModeEnum.None };
        closeCard.Pressed += () =>
        {
            _detailsExpanded = false;
            UpdateObjectCard();
        };
        cardHeader.AddChild(closeCard);
        _objectName = new Label { ThemeTypeVariation = "WorkspaceTitle", ClipText = true };
        facts.AddChild(_objectName);
        var actions = new HBoxContainer();
        facts.AddChild(actions);
        _goTo = CameraButton(actions, "GoToObject", "Go to", "Fly to the selected object · Esc cancels the flight", goTo);
        _track = CameraButton(actions, "TrackObject", "Track", "Keep the selected object centered as time advances", track);
        facts.AddChild(new HSeparator());
        facts.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Current state", "Текущее состояние")));
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
            _detailsToggle.Text = target?.Name ?? "";
            UpdateObjectCard();
        }
        _goTo.Disabled = star ? !surfaceAvailable : target is null && !flying;
        _track.Visible = !star;
        _track.Disabled = target is null || star;
        _goTo.Text = star ? WorkspaceText.Choose("Point at", "Навести камеру")
            : flying ? WorkspaceText.Choose("Stop flight", "Остановить перелёт") : WorkspaceText.Choose("Go to", "Перелететь");
        _goTo.TooltipText = star ? "Point the Earth observer at this star" : "Fly to the selected object · Esc cancels the flight";
        _track.Text = tracking ? WorkspaceText.Choose("Stop tracking", "Прекратить слежение") : WorkspaceText.Choose("Track", "Следить");
        if (target is null)
            return;
        _objectKind.Text = target.Kind switch
        {
            CatalogueKind.DwarfPlanet => "DWARF PLANET",
            CatalogueKind.SurfaceLocation => "SURFACE LOCATION",
            _ => target.Kind.ToString().ToUpperInvariant()
        };
        _objectName.Text = target.Name;
        _objectDetails.Text = details;
    }

    private void UpdateObjectCard()
    {
        _detailsToggle.Visible = _shownObject is not null;
        _detailsToggle.SetPressedNoSignal(_detailsExpanded);
        _detailsToggle.TooltipText = _detailsExpanded
            ? WorkspaceText.Choose("Collapse object details", "Свернуть сведения об объекте")
            : WorkspaceText.Choose("Show object details", "Показать сведения об объекте");
        _card.Visible = _shownObject is not null && _detailsExpanded;
    }

    internal VBoxContainer AddPage(string id, string title, string? group = null, string? tabTitle = null)
    {
        var button = new Button
        {
            Name = id + "Tab", TooltipText = tabTitle ?? title, ToggleMode = true,
            ThemeTypeVariation = "RailButton",
            CustomMinimumSize = new(30, 30), FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        Button? overview = null;
        if (group is null)
        {
            var icon = new WorkspaceGlyph { Page = id, MouseFilter = Control.MouseFilterEnum.Ignore };
            button.AddChild(icon);
            icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _rail.AddChild(button);
            overview = new Button { Text = tabTitle ?? title, ToggleMode = true };
            overview.Name = id + "Overview";
            overview.Pressed += () => Open(id);
            _sectionTabs.AddChild(overview);
        }
        else
        {
            button.Text = tabTitle ?? title;
            _sectionTabs.AddChild(button);
        }
        button.Pressed += () => Open(group is null && _selectedPage == id ? null : id);
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
        _pages.Add(id, new(button, content, title, group ?? id, overview));
        return content;
    }

    internal void AddRailSeparator() => _rail.AddChild(new HSeparator());

    internal void AddRailSpacer() =>
        _rail.AddChild(new Control { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

    internal void Open(string? id)
    {
        _selectedPage = id;
        string? group = id is null ? null : _pages[id].Group;
        foreach (var (key, page) in _pages)
        {
            bool selected = key == id;
            ((Control)page.Content.GetParent()).Visible = selected;
            if (page.Overview is { } overview)
            {
                page.Button.SetPressedNoSignal(key == group);
                overview.Visible = key == group && _pages.Values.Count(item => item.Group == group) > 1;
                overview.SetPressedNoSignal(selected);
            }
            else
            {
                page.Button.Visible = page.Group == group;
                page.Button.SetPressedNoSignal(selected);
            }
            if (selected)
                _title.Text = _pages[page.Group].Title;
        }
        _shortcut.Text = id switch { "Search" => "Enter", "Time" => "Space", _ => "" };
        _shortcut.Visible = _shortcut.Text.Length != 0;
        _sidebar.Visible = id is not null;
    }
}
