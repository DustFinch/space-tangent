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
    private readonly Dictionary<ObjectId, Button> _rows = [];
    private readonly ButtonGroup _filters = new();
    private ObjectCatalogue _catalogue;
    private CatalogueObject? _first;
    private ObjectId? _selection;
    private int _filter;

    internal ObjectSearchPanel(VBoxContainer panel, ObjectCatalogue catalogue, Action<ObjectId> select)
    {
        _catalogue = catalogue;
        _select = select;
        _query = new LineEdit { Name = "ObjectQuery", PlaceholderText = "Name or catalogue ID", ClearButtonEnabled = true };
        _query.TextChanged += _ => Refresh();
        _query.TextSubmitted += _ =>
        {
            if (_first is { } match)
            {
                _select(match.Id);
                _query.ReleaseFocus();
            }
        };
        panel.AddChild(_query);
        var filters = new HBoxContainer();
        filters.AddThemeConstantOverride("separation", 0);
        panel.AddChild(filters);
        AddFilter(filters, "All", 0);
        AddFilter(filters, "Bodies", 1);
        AddFilter(filters, "Spacecraft", 2);
        AddFilter(filters, "Stars", 3);
        _resultCount = SimulatorTheme.Section("Solar System and add-ons");
        panel.AddChild(_resultCount);
        _results = new VBoxContainer { Name = "ObjectResults" };
        _results.AddThemeConstantOverride("separation", 1);
        panel.AddChild(_results);
        Refresh();
    }

    internal void Focus() => _query.GrabFocus();

    internal void Replace(ObjectCatalogue catalogue)
    {
        if (_catalogue == catalogue)
            return;
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

    private void AddFilter(HBoxContainer filters, string label, int filter)
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
        var matches = _catalogue.Search(_query.Text).Where(body => _filter switch
        {
            1 => body.Kind is CatalogueKind.Planet or CatalogueKind.Moon || body.Id.Value == "sol/sun",
            2 => body.Kind == CatalogueKind.Spacecraft,
            3 => body.Kind == CatalogueKind.Star,
            _ => !browsing || !body.Id.Value.StartsWith("hip/", StringComparison.Ordinal)
        }).ToArray();
        _first = matches.FirstOrDefault();
        _resultCount.Text = matches.Length > 100 ? $"FIRST 100 OF {matches.Length}" : $"OBJECTS · {matches.Length}";
        if (_first is null)
            _results.AddChild(SimulatorTheme.Detail("No matching objects."));
        bool showHierarchy = _filter == 0 && string.IsNullOrWhiteSpace(_query.Text);
        foreach (CatalogueObject body in matches.Take(100))
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
            row.AddChild(button);
            _rows.Add(body.Id, button);
        }
    }
}
