// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.UI;

internal sealed class ObjectSearchPanel
{
    private readonly LineEdit _query;
    private readonly VBoxContainer _results;
    private readonly Label _resultCount;
    private readonly Action<ObjectId> _select;
    private readonly Action<ObjectId> _navigate;
    private readonly Func<ObjectId, (string Label, bool Enabled)> _navigationAction;
    private readonly PopupMenu _actions;
    private readonly Dictionary<ObjectId, Button> _rows = [];
    private readonly ButtonGroup _filters = new();
    private ObjectCatalogue _catalogue;
    private CatalogueObject? _first;
    private ObjectId? _selection;
    private ObjectId? _menuTarget;
    private int _filter;
    // Searching scans the whole catalogue. With the star archives installed that is hundreds of thousands of entries,
    // so typing waits for a short pause before searching.
    private const int LargeCatalogue = 20_000;
    private readonly global::Godot.Timer _debounce = new() { OneShot = true, WaitTime = .15 };

    internal ObjectSearchPanel(VBoxContainer panel, ObjectCatalogue catalogue, Action<ObjectId> select, Func<string, bool> aimAtMarker,
        Action<ObjectId> navigate, Func<ObjectId, (string Label, bool Enabled)> navigationAction)
    {
        _catalogue = catalogue;
        _select = select;
        _navigate = navigate;
        _navigationAction = navigationAction;
        _actions = new PopupMenu { Name = "ObjectActions", PreferNativeMenu = false };
        panel.AddChild(_actions);
        _actions.IdPressed += _ =>
        {
            if (_menuTarget is { } target)
                _navigate(target);
        };
        _query = new LineEdit
        {
            Name = "ObjectQuery", ClearButtonEnabled = true,
            PlaceholderText = WorkspaceText.Choose("Name or catalogue ID", "Имя или ID каталога")
        };
        panel.AddChild(_debounce);
        _debounce.Timeout += () => Refresh();
        _query.TextChanged += _ =>
        {
            if (_catalogue.Objects.Count > LargeCatalogue)
                _debounce.Start();
            else
                Refresh();
        };
        _query.TextSubmitted += text =>
        {
            if (!_debounce.IsStopped())
            {
                _debounce.Stop();
                Refresh();
            }
            if (aimAtMarker(text))
                _query.ReleaseFocus();
            else if (_first is { } match)
            {
                _select(match.Id);
                _query.ReleaseFocus();
            }
        };
        panel.AddChild(_query);
        var filters = new HFlowContainer();
        filters.AddThemeConstantOverride("h_separation", 2);
        panel.AddChild(filters);
        AddFilter(filters, WorkspaceText.Choose("All", "Все"), 0);
        AddFilter(filters, WorkspaceText.Choose("Bodies", "Тела"), 1);
        AddFilter(filters, WorkspaceText.Choose("Spacecraft", "Аппараты"), 2);
        AddFilter(filters, WorkspaceText.Choose("Stars", "Звёзды"), 3);
        AddFilter(filters, WorkspaceText.Choose("Deep sky", "Дальний космос"), 4);
        _resultCount = SimulatorTheme.Section("Solar System and add-ons");
        panel.AddChild(_resultCount);
        _results = new VBoxContainer { Name = "ObjectResults" };
        _results.AddThemeConstantOverride("separation", 1);
        panel.AddChild(_results);
        Refresh();
    }

    internal void Focus() => _query.GrabFocus();

    private void OpenActions(ObjectId target, Vector2 screenPosition)
    {
        _query.ReleaseFocus();
        _select(target);
        _menuTarget = target;
        var (label, enabled) = _navigationAction(target);
        _actions.Clear();
        _actions.AddItem(label, 0);
        _actions.SetItemDisabled(0, !enabled);
        _actions.ResetSize();
        _actions.Position = (Vector2I)screenPosition;
        _actions.Popup();
    }

    internal void Replace(ObjectCatalogue catalogue)
    {
        if (_catalogue == catalogue)
            return;
        _actions.Hide();
        _catalogue = catalogue;
        Refresh();
    }

    internal void UpdateSelection(ObjectId? selection)
    {
        if (_selection == selection)
            return;
        _selection = selection;
        foreach (var (id, row) in _rows)
            row.SetPressedNoSignal(id == selection);
    }

    private void AddFilter(Container filters, string label, int filter)
    {
        var button = new Button
        {
            Text = label, ToggleMode = true, ButtonGroup = _filters, ButtonPressed = filter == 0,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None
        };
        button.Pressed += () => { _filter = filter; Refresh(); };
        filters.AddChild(button);
    }

    private void Refresh()
    {
        foreach (Node child in _results.GetChildren())
        {
            _results.RemoveChild(child);
            child.QueueFree();
        }
        _rows.Clear();
        bool browsing = string.IsNullOrWhiteSpace(_query.Text);
        var page = _catalogue.SearchPage(_query.Text, 100, body => (!browsing || body.Kind != CatalogueKind.SurfaceLocation) && (_filter switch
        {
            1 => body.Kind is CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.DwarfPlanet or CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.SurfaceLocation || body.Id.Value == "sol/sun",
            2 => body.Kind == CatalogueKind.Spacecraft,
            3 => body.Kind == CatalogueKind.Star,
            4 => body.Kind is CatalogueKind.Galaxy or CatalogueKind.Nebula or CatalogueKind.Cluster,
            _ => !browsing || body.Kind != CatalogueKind.Star || body.Id.Value == "sol/sun"
        }));
        var matches = page.Items;
        _first = matches.FirstOrDefault();
        _resultCount.Text = page.Total > 100
            ? $"{WorkspaceText.Choose("FIRST 100 OF", "ПЕРВЫЕ 100 ИЗ")} {page.Total}"
            : $"{WorkspaceText.Choose("OBJECTS", "ОБЪЕКТЫ")} · {page.Total}";
        if (_first is null)
            _results.AddChild(SimulatorTheme.Detail(WorkspaceText.Choose("No matching objects.", "Объекты не найдены.")));
        bool showHierarchy = _filter == 0 && string.IsNullOrWhiteSpace(_query.Text);
        foreach (CatalogueObject body in matches)
        {
            var row = new HBoxContainer();
            _results.AddChild(row);
            if (showHierarchy && body.Parent is { } parent)
            {
                int indent = _catalogue.Find(parent)?.Parent is null ? 12 : 24;
                row.AddChild(new Control { CustomMinimumSize = new(indent, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
            }
            var button = new Button
            {
                Text = body.Name, TooltipText = body.Id.Value, ToggleMode = true, ClipText = true,
                ButtonPressed = body.Id == _selection, ThemeTypeVariation = "RailButton",
                Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                FocusMode = Control.FocusModeEnum.None
            };
            button.Pressed += () =>
            {
                _select(body.Id);
                button.SetPressedNoSignal(true);
            };
            button.GuiInput += input =>
            {
                if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } click)
                {
                    button.AcceptEvent();
                    OpenActions(body.Id, button.GetScreenTransform() * click.Position);
                }
            };
            row.AddChild(button);
            _rows.Add(body.Id, button);
        }
    }
}
