// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application;

public sealed class LuaCatalogueFactories : ISscScriptModels
{
    private readonly Dictionary<string, FactoryEnvironment> _roots = new(StringComparer.Ordinal);

    public SscScriptOrbit? CreateOrbit(SscScriptRequest request)
    {
        var environment = ForRoot(request.Root);
        return environment.Create(request, orbit: true) is { } instance
            ? new(epoch => instance.Read<Vector3d>(epoch, "position", values =>
            {
                var position = new Vector3d(Component(values, 0), Component(values, 1), Component(values, 2));
                return position.IsFinite && position.Length <= 1e19 ? position : null;
            })) : null;
    }

    public SscScriptRotation? CreateRotation(SscScriptRequest request)
    {
        var environment = ForRoot(request.Root);
        return environment.Create(request, orbit: false) is { } instance
            ? new(epoch => instance.Read<SscScriptQuaternion>(epoch, "orientation", values =>
            {
                double w = Component(values, 0), x = Component(values, 1), y = Component(values, 2), z = Component(values, 3);
                return w != 0 || x != 0 || y != 0 || z != 0 ? new SscScriptQuaternion(w, x, y, z) : null;
            })) : null;
    }

    private FactoryEnvironment ForRoot(string root)
    {
        if (!_roots.TryGetValue(root, out var environment))
            _roots.Add(root, environment = new(root));
        return environment;
    }

    private static double Component(DynValue value, int index)
    {
        var part = value.Type == DataType.Tuple && index < value.Tuple.Length ? value.Tuple[index] :
            index == 0 && value.Type != DataType.Tuple ? value : DynValue.Nil;
        double number = part.CastToNumber() ?? 0;
        return double.IsFinite(number) ? number : throw new ScriptRuntimeException("Scripted model returned a non-finite component.");
    }

    private sealed class FactoryEnvironment
    {
        internal readonly Script Script;
        internal readonly object Gate = new();
        private readonly string _root;
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly DynValue _lookup;
        private bool _loading;
        private int _sourceBytes;

        internal FactoryEnvironment(string root)
        {
            _root = root;
            Script = new(CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators |
                CoreModules.Metatables | CoreModules.Math | CoreModules.String);
            Script.Globals["collectgarbage"] = DynValue.Nil;
            Script.Globals["print"] = DynValue.Nil;
            Table strings = Script.Globals.Get("string").Table;
            strings["rep"] = DynValue.Nil;
            strings["format"] = DynValue.Nil;
            strings["dump"] = DynValue.Nil;
            _lookup = BoundedLuaCall.Invoke(Script, Script.LoadString("return function(t, k) return t[k] end"));
            DynValue bootstrap = Script.LoadString("""
                return function(load_local)
                    local cache, loading = {}, {}
                    return function(name)
                        if cache[name] ~= nil then return cache[name] end
                        if loading[name] then error("Circular local module " .. name) end
                        loading[name] = true
                        local chunk = load_local(name)
                        local value = chunk()
                        if value == nil then value = true end
                        loading[name], cache[name] = nil, value
                        return value
                    end
                end
                """);
            var makeRequire = BoundedLuaCall.Invoke(Script, bootstrap);
            Script.Globals["require"] = BoundedLuaCall.Invoke(Script, makeRequire, DynValue.NewCallback((_, args) =>
                Module(args[0])));
        }

        internal ModelInstance? Create(SscScriptRequest request, bool orbit)
        {
            lock (Gate)
            {
                _loading = true;
                try
                {
                    if (!_loaded.Contains(request.ModulePath))
                    {
                        BoundedLuaCall.Invoke(Script, ReadChunk(request.ModulePath));
                        _loaded.Add(request.ModulePath);
                    }
                    DynValue factory = Script.Globals.Get(request.Function);
                    if (factory.Type != DataType.Function)
                        return null;
                    var table = Map(request.Arguments);
                    table.Table["AddonPath"] = ToLua(request.Root);
                    DynValue made = BoundedLuaCall.Invoke(Script, factory, table);
                    if (made.Type != DataType.Table || orbit && ReadNumber(made, "boundingRadius") is not (> 0 and <= 1e19))
                        return null;
                    _ = ReadNumber(made, "period");
                    if (ReadNumber(made, "endDate") < ReadNumber(made, "beginDate"))
                        return null;
                    string callback = orbit ? "position" : "orientation";
                    return Field(made, callback).Type == DataType.Function ? new(this, made) : null;
                }
                catch (InterpreterException error)
                {
                    throw new InvalidDataException(FromLua(error.DecoratedMessage ?? error.Message), error);
                }
                finally { _loading = false; }
            }
        }

        internal DynValue Field(DynValue table, string name) =>
            BoundedLuaCall.Invoke(Script, _lookup, table, DynValue.NewString(name));

        private double ReadNumber(DynValue table, string name)
        {
            double value = Field(table, name).CastToNumber() ?? 0;
            return double.IsFinite(value) ? value : throw new ScriptRuntimeException($"Invalid Lua model {name}.");
        }

        private DynValue Module(DynValue value)
        {
            if (!_loading || value.Type != DataType.String)
                throw new ScriptRuntimeException("Local modules can be loaded during model initialization.");
            string name = value.String;
            if (name.Length is 0 or > 128 || name.Split('.').Any(part => part.Length == 0 ||
                part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))))
                throw new ScriptRuntimeException("Invalid local module name.");
            string file = name.Replace('.', Path.DirectorySeparatorChar) + ".lua";
            string local = Path.Combine(_root, "scripts", file);
            if (!AddonDependencies.FileExists(local))
                local = Path.Combine(_root, file);
            if (!AddonDependencies.FileExists(local))
                throw new ScriptRuntimeException($"Local module {name} is unavailable.");
            return ReadChunk(local);
        }

        private DynValue ReadChunk(string path)
        {
            string relative = Path.GetRelativePath(_root, path);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("A Lua model module must be inside its add-on.");
            for (string cursor = path; ; cursor = Path.GetDirectoryName(cursor)!)
            {
                if ((AddonDependencies.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A Lua module cannot be a symbolic link.");
                if (Path.GetRelativePath(_root, cursor) == ".")
                    break;
            }
            using var stream = AddonDependencies.OpenRead(path, 32 * 1024);
            if (stream.Length > 32 * 1024 || stream.Length + _sourceBytes > 256 * 1024)
                throw new InvalidDataException("Lua module source quota exceeded.");
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            _sourceBytes += bytes.Length;
            ReadOnlySpan<byte> content = bytes;
            if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
                content = content[3..];
            _ = StrictUtf8.GetCharCount(content);
            return Script.LoadString(Encoding.Latin1.GetString(content), codeFriendlyName: relative);
        }

        private DynValue Map(IReadOnlyDictionary<string, object> arguments)
        {
            var table = new Table(Script);
            foreach (var (key, value) in arguments)
                table[ToLua(key)] = ConvertArgument(value);
            return DynValue.NewTable(table);
        }

        private DynValue ConvertArgument(object value) => value switch
        {
            double number => DynValue.NewNumber(number),
            string text => DynValue.NewString(ToLua(text)),
            bool boolean => DynValue.NewBoolean(boolean),
            IReadOnlyDictionary<string, object> nested => Map(nested),
            IReadOnlyList<object> sequence => DynValue.NewTable(new Table(Script, sequence.Select(ConvertArgument).ToArray())),
            _ => DynValue.Nil
        };
    }

    private sealed class ModelInstance(FactoryEnvironment environment, DynValue model)
    {
        private readonly Dictionary<JulianInstant, object?> _cache = new();

        internal T? Read<T>(JulianInstant epoch, string field, Func<DynValue, T?> read) where T : struct
        {
            lock (environment.Gate)
            {
                if (_cache.TryGetValue(epoch, out object? cached))
                    return (T?)cached;
                T? result;
                try
                {
                    var function = environment.Field(model, field);
                    result = function.Type == DataType.Function ? read(BoundedLuaCall.Invoke(environment.Script, function,
                        model, DynValue.NewNumber(epoch.ToJulianDate()))) : null;
                }
                catch (InterpreterException) { result = null; }
                if (_cache.Count >= 4)
                    _cache.Remove(_cache.Keys.First());
                _cache.Add(epoch, result);
                return result;
            }
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static string ToLua(string value) => Encoding.Latin1.GetString(StrictUtf8.GetBytes(value));

    private static string FromLua(string value)
    {
        try { return value.All(character => character <= 255) ? StrictUtf8.GetString(Encoding.Latin1.GetBytes(value)) : value; }
        catch (DecoderFallbackException) { return value; }
    }
}
