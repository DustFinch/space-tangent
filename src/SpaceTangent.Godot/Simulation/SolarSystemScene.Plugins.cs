// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private readonly Dictionary<string, ObserverLuaPlugin> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Action> _pluginMenuActions = new();
    private FileDialog _tourConversionSource = null!;

    internal ObserverLuaPlugin? OpenPlugin(string file)
    {
        try
        {
            var manifest = ObserverPluginManifest.Load(file);
            if (_plugins.Count >= 8 && !_plugins.ContainsKey(manifest.Id))
                throw new InvalidOperationException("At most eight plugins can run at once.");
            var plugin = new ObserverLuaPlugin(manifest, Session!, _active, _surfaceTextures is not null,
                line => GD.Print($"Plugin {manifest.Id}: {line}"));
            if (_plugins.Remove(manifest.Id, out var previous) && previous.Disable())
                Refresh();
            _plugins.Add(manifest.Id, plugin);
            UpdatePluginMenu();
            _status.Text = $"Plugin enabled · {manifest.Name}";
            return plugin;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open plugin. {error.Message}";
            return null;
        }
    }

    private bool StepPlugins(double elapsed)
    {
        if (_plugins.Count == 0)
            return false;
        bool changed = false, registrationChanged = false;
        foreach (var (id, plugin) in _plugins.ToArray())
        {
            int version = plugin.RegistrationVersion;
            string? previousError = plugin.Error;
            changed |= plugin.Step(elapsed);
            if (previousError != plugin.Error && plugin.Error is { } error)
            {
                _status.Text = $"{plugin.Manifest.Name} · {error}";
                GD.Print($"Plugin {id}: {error}");
            }
            if (!plugin.Enabled)
                _plugins.Remove(id);
            registrationChanged |= !plugin.Enabled || version != plugin.RegistrationVersion;
        }
        if (registrationChanged)
            UpdatePluginMenu();
        return changed;
    }

    private void DisablePlugins(bool updateMenu = true)
    {
        foreach (var plugin in _plugins.Values)
            plugin.Disable();
        _plugins.Clear();
        if (updateMenu && IsInsideTree())
            UpdatePluginMenu();
    }

    private void UpdatePluginMenu()
    {
        while (_scriptMenu.ItemCount > 3)
            _scriptMenu.RemoveItem(_scriptMenu.ItemCount - 1);
        _pluginMenuActions.Clear();
        _tourConversionSource.ClearFilters();
        _tourConversionSource.AddFilter("*.cel,*.celx", "Tour sources");
        int menuId = 100;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in _plugins.Values)
        {
            string name = plugin.Manifest.Name;
            _scriptMenu.AddSeparator(name);
            foreach (var entry in plugin.Manifest.Scripts)
                Add(entry.Title, () => plugin.StartFile(entry.File));
            foreach (var command in plugin.Commands)
                Add(command.Title, () => plugin.RunCommand(command.Id));
            Add("Disable " + name, () =>
            {
                _plugins.Remove(plugin.Manifest.Id);
                if (plugin.Disable())
                    Refresh();
                UpdatePluginMenu();
                _status.Text = $"Plugin disabled · {name}";
            });
            foreach (var importer in plugin.Importers)
                if (extensions.Add(importer.Extension))
                    _tourConversionSource.AddFilter("*" + importer.Extension, importer.Title);
        }

        void Add(string title, Action invoke)
        {
            _scriptMenu.AddItem(title, menuId);
            _pluginMenuActions.Add(menuId++, invoke);
        }
    }

    private void RunPluginMenuAction(long id)
    {
        if (!_pluginMenuActions.TryGetValue(id, out var invoke))
            return;
        try { invoke(); }
        catch (Exception error) when (IsLoadError(error)) { _status.Text = $"Could not run plugin. {error.Message}"; }
    }

    private TourConversion ConvertTourFile(string path)
    {
        string extension = Path.GetExtension(path);
        var plugin = _plugins.Values.FirstOrDefault(item =>
            item.Importers.Any(importer => importer.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)));
        if (plugin is null)
            return TourConverter.ConvertFile(path);
        using var file = File.OpenRead(path);
        if (file.Length > 32 * 1024)
            throw new InvalidDataException("Plugin importer input exceeds its size quota.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        string? result = plugin.Convert(extension, reader.ReadToEnd(), Path.GetFileName(path));
        return result is null ? throw new InvalidDataException("The plugin importer is unavailable.") : new(result, 0, []);
    }
}
