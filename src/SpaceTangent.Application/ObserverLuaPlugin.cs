// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed class ObserverLuaPlugin
{
    private const int MaxJobs = 16;
    private sealed record Job(string Label, LuaSessionScript.Invocation Work);
    private sealed record Command(string Title, DynValue Function);
    private sealed record Importer(string Title, DynValue Function);

    private readonly LuaSessionScript _runtime;
    private readonly SimulationSession _session;
    private readonly Script _vm;
    private readonly Action? _clearMedia;
    private readonly List<Job> _jobs = [];
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DynValue> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Importer> _importers = new(StringComparer.OrdinalIgnoreCase);
    private ObjectId? _selection;
    private int _moduleBytes;
    private bool _enabled = true;

    public ObserverPluginManifest Manifest { get; }
    public string? Error { get; private set; }
    public int View => _runtime.View;
    public bool Enabled => _enabled;
    public bool HasJobs => _jobs.Count != 0;
    public int RegistrationVersion { get; private set; }
    public IEnumerable<(string Id, string Title)> Commands => _commands.Select(pair => (pair.Key, pair.Value.Title));
    public IEnumerable<(string Extension, string Title)> Importers => _importers.Select(pair => (pair.Key, pair.Value.Title));

    public ObserverLuaPlugin(ObserverPluginManifest manifest, SimulationSession session, int view, bool hasSurface,
        Action<string> log, Action<ScriptMediaRequest>? media = null, Action? clearMedia = null)
    {
        Manifest = manifest;
        _session = session;
        _clearMedia = clearMedia;
        _runtime = LuaSessionScript.PluginRuntime(session, view, hasSurface, log, media);
        _vm = _runtime.Interpreter;
        _selection = session[view].Selection;
        InstallApi();
        if (manifest.Main is { } main)
            StartFile(main);
    }

    public void StartFile(string file)
    {
        if (!_enabled || (file != Manifest.Main && !Manifest.Scripts.Any(entry => entry.File == file)))
            throw new InvalidOperationException("The plugin script is not declared in its manifest.");
        try
        {
            if (!Queue(file, _vm.LoadString(Manifest.ReadLua(file), codeFriendlyName: file), []))
                throw new InvalidOperationException("This plugin has too many active invocations.");
        }
        catch (InterpreterException error)
        {
            throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error);
        }
    }

    public void RunCommand(string id)
    {
        if (!_commands.TryGetValue(id, out var command) || !Queue(id, command.Function, []))
            throw new InvalidOperationException("The plugin command is unavailable or busy.");
    }

    public string? Convert(string extension, string input, string name)
    {
        if (!_enabled || !_importers.TryGetValue(extension, out var importer))
            return null;
        if (Encoding.UTF8.GetByteCount(input) > 32 * 1024)
            throw new InvalidDataException("Plugin importer input exceeds its size quota.");
        try
        {
            var result = BoundedLuaCall.Invoke(_vm, importer.Function, DynValue.NewString(input), DynValue.NewString(name));
            if (result.Type != DataType.String || Encoding.UTF8.GetByteCount(result.String) > 32 * 1024)
                throw new InvalidDataException("The importer must return Lua text within its size quota.");
            _ = _vm.LoadString(result.String, codeFriendlyName: name);
            return result.String;
        }
        catch (InterpreterException error)
        {
            throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error);
        }
    }

    public bool Step(double elapsed)
    {
        if (!_enabled)
            return false;
        if (View < 0)
        {
            _jobs.Clear();
            _enabled = false;
            _clearMedia?.Invoke();
            Error = "The plugin observer was closed.";
            return false;
        }

        var selected = _session[View].Selection;
        if (selected != _selection)
        {
            _selection = selected;
            if (_events.TryGetValue("select", out var select))
                Queue("event:select", select, [_runtime.LuaObject(selected)]);
        }
        if (_events.TryGetValue("tick", out var tick) && !_jobs.Any(job => job.Label == "event:tick"))
            Queue("event:tick", tick, [DynValue.NewNumber(elapsed)]);
        if (_jobs.Count == 0)
            return false;

        bool changed = false;
        int slice = 1024 / _jobs.Count;
        foreach (var job in _jobs.ToArray())
        {
            changed |= _runtime.Step(job.Work, elapsed, slice);
            if (!job.Work.Complete)
                continue;
            _jobs.Remove(job);
            if (job.Work.Error is { } error)
                Error = $"{job.Label}: {error}";
        }
        return changed;
    }

    public bool Disable()
    {
        if (!_enabled)
            return false;
        _enabled = false;
        _jobs.Clear();
        bool changed = false;
        if (View >= 0 && _events.TryGetValue("disable", out var cleanup))
        {
            var work = _runtime.NewInvocation(cleanup);
            changed = _runtime.Step(work, 0, 4096);
            if (work.Error is { } error)
                Error = error;
            else if (!work.Complete)
                Error = "Plugin cleanup must finish without waiting.";
        }
        _clearMedia?.Invoke();
        _commands.Clear();
        _events.Clear();
        _importers.Clear();
        RegistrationVersion++;
        return changed;
    }

    private bool Queue(string label, DynValue function, DynValue[] args)
    {
        if (!_enabled || _jobs.Count >= MaxJobs)
            return false;
        _jobs.Add(new(label, _runtime.NewInvocation(function, args)));
        return true;
    }

    private void InstallApi()
    {
        var api = new Table(_vm);
        api["id"] = Manifest.Id;
        api["name"] = Manifest.Name;
        api["version"] = Manifest.Version;
        Bind("command", args =>
        {
            string id = Text(args[0]), title = Title(args[1]);
            if (!ObserverPluginManifest.ValidId(id) || _commands.Count >= 32 && !_commands.ContainsKey(id))
                throw new ScriptRuntimeException("Invalid plugin command ID or command quota exceeded.");
            _commands[id] = new(title, Function(args[2]));
            RegistrationVersion++;
            return DynValue.Nil;
        });
        Bind("on", args =>
        {
            string name = Text(args[0]);
            if (name is not ("select" or "tick" or "disable"))
                throw new ScriptRuntimeException("Unsupported plugin event.");
            if (args[1].IsNil())
                _events.Remove(name);
            else
                _events[name] = Function(args[1]);
            return DynValue.Nil;
        });
        Bind("importer", args =>
        {
            string extension = Text(args[0]);
            if (extension.Length is < 2 or > 15 || extension[0] != '.' ||
                !extension.AsSpan(1).ToString().All(char.IsAsciiLetterOrDigit) ||
                _importers.Count >= 16 && !_importers.ContainsKey(extension))
                throw new ScriptRuntimeException("Invalid plugin file extension or importer quota exceeded.");
            _importers[extension] = new(Title(args[2]), Function(args[1]));
            RegistrationVersion++;
            return DynValue.Nil;
        });
        Bind("run", args => DynValue.NewBoolean(Queue("job", Function(args[0]), args.GetArray(1))));
        Bind("read", args => DynValue.NewString(Manifest.ReadLua(Text(args[0]))));
        _vm.Globals["plugin"] = DynValue.NewTable(api);

        var requireBootstrap = _vm.LoadString("""
            return function(loader)
                local cache, loading = {}, {}
                return function(name)
                    if cache[name] ~= nil then return cache[name] end
                    if loading[name] then error("Circular plugin module " .. name) end
                    loading[name] = true
                    local chunk = loader(name)
                    local result = chunk()
                    if result == nil then result = true end
                    loading[name], cache[name] = nil, result
                    return result
                end
            end
            """);
        var requireFactory = BoundedLuaCall.Invoke(_vm, requireBootstrap);
        _vm.Globals["require"] = BoundedLuaCall.Invoke(_vm, requireFactory, DynValue.NewCallback((_, args) =>
        {
            string name = Text(args[0]);
            if (name.Length is 0 or > 128 || name.Split('.').Any(part => !ObserverPluginManifest.ValidId(part)))
                throw new ScriptRuntimeException("Invalid plugin module name.");
            string file = name.Replace('.', '/') + ".lua";
            string source = Manifest.ReadLua(file);
            _moduleBytes += Encoding.UTF8.GetByteCount(source);
            if (_moduleBytes > 256 * 1024)
                throw new ScriptRuntimeException("Plugin module source quota exceeded.");
            return _vm.LoadString(source, codeFriendlyName: file);
        }));

        void Bind(string name, Func<CallbackArguments, DynValue> function) =>
            api[name] = DynValue.NewCallback((_, args) => function(args));
    }

    private static DynValue Function(DynValue value) => value.Type == DataType.Function ? value :
        throw new ScriptRuntimeException("Expected a Lua function.");
    private static string Text(DynValue value) => value.Type == DataType.String ? value.String :
        throw new ScriptRuntimeException("Expected text.");
    private static string Title(DynValue value) => Text(value) is { Length: > 0 and <= 160 } title && !title.Any(char.IsControl)
        ? title : throw new ScriptRuntimeException("Invalid plugin label.");
}
