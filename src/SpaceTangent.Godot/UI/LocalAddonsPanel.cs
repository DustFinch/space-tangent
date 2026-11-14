// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.UI;

internal sealed class LocalAddonsPanel
{
    private readonly VBoxContainer _rows;
    private readonly OptionButton _kind;
    private readonly Button _browse, _reload, _updateSatellites;
    private readonly FileDialog _directory, _mission;
    private readonly Func<Task<bool>> _reloadAction;
    private readonly Func<LocalAddonKind, bool, Task> _setDefault;
    private readonly Func<LocalAddonSource, bool, Task> _setSource;
    private readonly List<CheckButton> _switches = [];
    private bool _busy, _updatingSatellites;

    internal LocalAddonsPanel(Control owner, Container page, Func<Task<bool>> reload,
        Func<LocalAddonKind, string, Task> add, Func<LocalAddonKind, bool, Task> setDefault,
        Func<LocalAddonSource, bool, Task> setSource, Func<Task> updateSatellites)
    {
        _reloadAction = reload;
        _setDefault = setDefault;
        _setSource = setSource;
        _reload = new Button { Name = "ReloadAddOns", Text = WorkspaceText.Choose("Reload add-ons", "Перечитать дополнения") };
        _reload.Pressed += async () => await _reloadAction();
        page.AddChild(_reload);

        var import = new Button
        {
            Name = "AddonImportOptions", Text = WorkspaceText.Choose("Import and update", "Импорт и обновление"),
            ToggleMode = true
        };
        page.AddChild(import);
        var importTools = new VBoxContainer { Visible = false };
        page.AddChild(importTools);
        import.Toggled += expanded => importTools.Visible = expanded;
        importTools.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Add a local source", "Добавить локальный источник")));
        _kind = new OptionButton { Name = "LocalAddonKind" };
        foreach (LocalAddonKind kind in Enum.GetValues<LocalAddonKind>())
            _kind.AddItem(Title(kind), (int)kind);
        importTools.AddChild(_kind);

        _directory = new FileDialog { Name = "OpenAddonFolder", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenDir, DeletingEnabled = false };
        _mission = new FileDialog { Name = "OpenAddonMission", Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile, DeletingEnabled = false };
        _mission.AddFilter("*.ssc,*.json", "Mission catalogues");
        owner.AddChild(_directory);
        owner.AddChild(_mission);
        _directory.DirSelected += async path => await add(Kind, path);
        _mission.FileSelected += async path => await add(LocalAddonKind.Mission, path);
        _browse = new Button { Name = "BrowseLocalAddon", Text = WorkspaceText.Choose("Choose folder…", "Выбрать папку…") };
        _kind.ItemSelected += _ => _browse.Text = Kind == LocalAddonKind.Mission ? WorkspaceText.Choose("Choose file…", "Выбрать файл…")
            : WorkspaceText.Choose("Choose folder…", "Выбрать папку…");
        _browse.Pressed += () => (Kind == LocalAddonKind.Mission ? _mission : _directory).PopupCentered(new(720, 470));
        importTools.AddChild(_browse);
        _updateSatellites = new Button { Name = "UpdateSatelliteGroups", Text = WorkspaceText.Choose("Update satellite groups", "Обновить группы спутников") };
        _updateSatellites.Pressed += async () =>
        {
            _updatingSatellites = true;
            SetBusy(_busy);
            try { await updateSatellites(); }
            finally
            {
                _updatingSatellites = false;
                if (GodotObject.IsInstanceValid(_updateSatellites))
                    SetBusy(_busy);
            }
        };
        importTools.AddChild(_updateSatellites);
        page.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Local sources", "Локальные источники")));
        _rows = new VBoxContainer { Name = "LocalAddonSources" };
        _rows.AddThemeConstantOverride("separation", 9);
        page.AddChild(_rows);
    }

    internal void SetBusy(bool busy)
    {
        _busy = busy;
        bool disabled = busy || _updatingSatellites;
        _kind.Disabled = _browse.Disabled = _reload.Disabled = _updateSatellites.Disabled = disabled;
        foreach (CheckButton toggle in _switches)
            toggle.Disabled = disabled;
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
            AddSource($"LocalAddon{index}", display, source.Enabled && loaded?.FilteredPacks.ContainsKey(path) != true, path.Explicit!,
                loaded?.HasSource(source.Kind, path) == true, enabled => _setSource(source, enabled), Title(source.Kind));
        }
        foreach (LocalAddonKind kind in Enum.GetValues<LocalAddonKind>())
        {
            LocalDataPath path = AddonLoader.DefaultPath(kind);
            string location = path.Resolve();
            if (kind == LocalAddonKind.Mission && path.Explicit is null && !File.Exists(location))
                location = path.WithExtension(".ssc").Resolve();
            AddSource($"DefaultAddon{kind}", Title(kind), settings.UsesDefault(kind) && loaded?.FilteredPacks.ContainsKey(path) != true, location,
                loaded?.HasSource(kind, path) == true, enabled => _setDefault(kind, enabled), "Default");
        }
    }

    private LocalAddonKind Kind => (LocalAddonKind)_kind.GetSelectedId();

    private void AddSource(string name, string title, bool enabled, string path, bool loaded, Func<bool, Task> apply,
        string category)
    {
        var row = new VBoxContainer();
        _rows.AddChild(row);
        var toggle = new CheckButton { Name = name, Text = title, ButtonPressed = enabled, Disabled = _busy || _updatingSatellites,
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
        LocalAddonKind.BodyCatalog => "SSC body catalogue",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
