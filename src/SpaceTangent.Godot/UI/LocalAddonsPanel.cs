// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.UI;

internal sealed class LocalAddonsPanel
{
    private readonly VBoxContainer _rows;
    private readonly OptionButton _kind;
    private readonly Button _browse, _reload;
    private readonly FileDialog _directory, _mission;
    private readonly Func<Task<bool>> _reloadAction;
    private readonly Func<LocalAddonKind, bool, Task> _setDefault;
    private readonly Func<LocalAddonSource, bool, Task> _setSource;
    private readonly List<CheckButton> _switches = [];
    private bool _busy;

    internal LocalAddonsPanel(Control owner, Container page, Func<Task<bool>> reload,
        Func<LocalAddonKind, string, Task> add, Func<LocalAddonKind, bool, Task> setDefault,
        Func<LocalAddonSource, bool, Task> setSource)
    {
        _reloadAction = reload;
        _setDefault = setDefault;
        _setSource = setSource;
        page.AddChild(SimulatorTheme.Section("Add a local source"));
        _kind = new OptionButton { Name = "LocalAddonKind" };
        foreach (LocalAddonKind kind in Enum.GetValues<LocalAddonKind>())
            _kind.AddItem(Title(kind), (int)kind);
        page.AddChild(_kind);

        _directory = new FileDialog { Name = "OpenAddonFolder", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenDir, DeletingEnabled = false };
        _mission = new FileDialog { Name = "OpenAddonMission", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        _mission.AddFilter("*.ssc,*.json", "Mission catalogues");
        owner.AddChild(_directory);
        owner.AddChild(_mission);
        _directory.DirSelected += async path => await add(Kind, path);
        _mission.FileSelected += async path => await add(LocalAddonKind.Mission, path);
        _browse = new Button { Name = "BrowseLocalAddon", Text = "Choose folder…" };
        _kind.ItemSelected += _ => _browse.Text = Kind == LocalAddonKind.Mission ? "Choose file…" : "Choose folder…";
        _browse.Pressed += () => (Kind == LocalAddonKind.Mission ? _mission : _directory).PopupCentered(new(720, 470));
        page.AddChild(_browse);
        page.AddChild(SimulatorTheme.Section("Local sources"));
        _reload = new Button { Name = "ReloadAddOns", Text = "Reload add-ons" };
        _reload.Pressed += async () => await _reloadAction();
        page.AddChild(_reload);
        _rows = new VBoxContainer { Name = "LocalAddonSources" };
        _rows.AddThemeConstantOverride("separation", 9);
        page.AddChild(_rows);
    }

    internal void SetBusy(bool busy)
    {
        _busy = busy;
        _kind.Disabled = _browse.Disabled = _reload.Disabled = busy;
        foreach (CheckButton toggle in _switches)
            toggle.Disabled = busy;
    }

    internal void ShowSources(LocalAddonSettings settings, LocalAddonSettingsFile file, AddonLoader.DecodedAddOns? loaded)
    {
        foreach (Node row in _rows.GetChildren())
        {
            _rows.RemoveChild(row);
            row.QueueFree();
        }
        _switches.Clear();
        for (int index = 0; index < settings.Sources.Length; index++)
        {
            LocalAddonSource source = settings.Sources[index];
            LocalDataPath path = AddonLoader.SourcePath(source, file);
            string display = Path.GetFileName(source.Kind == LocalAddonKind.Mission
                ? Path.GetDirectoryName(path.Explicit!) : path.Explicit)!;
            AddSource($"LocalAddon{index}", display, source.Enabled, path.Explicit!,
                loaded?.HasSource(source.Kind, path) == true, enabled => _setSource(source, enabled), Title(source.Kind));
        }
        foreach (LocalAddonKind kind in Enum.GetValues<LocalAddonKind>())
        {
            LocalDataPath path = AddonLoader.DefaultPath(kind);
            string location = path.Resolve();
            if (kind == LocalAddonKind.Mission && path.Explicit is null && !File.Exists(location))
                location = path.WithExtension(".ssc").Resolve();
            AddSource($"DefaultAddon{kind}", Title(kind), settings.UsesDefault(kind), location,
                loaded?.HasSource(kind, path) == true, enabled => _setDefault(kind, enabled), "Default");
        }
    }

    private LocalAddonKind Kind => (LocalAddonKind)_kind.GetSelectedId();

    private void AddSource(string name, string title, bool enabled, string path, bool loaded, Func<bool, Task> apply,
        string category)
    {
        var row = new VBoxContainer();
        _rows.AddChild(row);
        var toggle = new CheckButton { Name = name, Text = title, ButtonPressed = enabled, Disabled = _busy,
            ClipText = true, TooltipText = path };
        row.AddChild(toggle);
        _switches.Add(toggle);
        string status = loaded ? "Loaded" : enabled ? "Unavailable" : "Disabled";
        var detail = SimulatorTheme.Detail($"{category} · {status}");
        detail.TooltipText = path;
        row.AddChild(detail);
        toggle.Toggled += async value => await apply(value);
    }

    private static string Title(LocalAddonKind kind) => kind switch
    {
        LocalAddonKind.Science => "Science satellites",
        LocalAddonKind.Mission => "SPICE mission",
        LocalAddonKind.Timeline => "Sampled timeline",
        LocalAddonKind.Terrain => "Lunar terrain",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
