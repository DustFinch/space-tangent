// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>Runs an explicitly opened observer script in bounded steps on its owning thread.</summary>
public sealed class LuaSessionScript
{
    private const int MaximumSourceBytes = 32 * 1024;
    private const int StepsPerFrame = 1024, MaximumSteps = 131072;
    private const long MaximumAllocatedBytes = 16 * 1024 * 1024;
    private const double MaximumWaitSeconds = 120;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SimulationSession _session;
    private readonly Action<string> _log;
    private readonly bool _hasSurface;
    private Coroutine? _coroutine;
    private int _steps, _messages;
    private long _allocated;
    private double _waitRemaining, _totalWait;
    private bool _changed;

    private LuaSessionScript(string source, string name, SimulationSession session, int view, bool hasSurface, Action<string> log)
    {
        _session = session;
        View = view;
        _hasSurface = hasSurface;
        _log = log;
        if (StrictUtf8.GetByteCount(source) > MaximumSourceBytes)
            throw new InvalidDataException("Lua source exceeds the size quota.");

        var script = new Script(CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators |
            CoreModules.Math | CoreModules.Bit32);
        script.Options.DebugPrint = Log;
        script.Globals.Set("collectgarbage", DynValue.Nil);
        var api = new Table(script);
        Bind(api, "pause", Pause);
        Bind(api, "settime", SetTime);
        Bind(api, "gettime", (_, _) => DynValue.NewNumber(_session[View].Epoch.ToJulianDate()));
        Bind(api, "synchronizetime", (_, arguments) => Change(() =>
            _session.Synchronize(Boolean(arguments[0]), View)));
        Bind(api, "mode", SetMode);
        Bind(api, "select", (_, arguments) => Change(() => _session.Select(View, new ObjectId(Text(arguments[0])))));
        Bind(api, "wait", Wait);
        Bind(api, "log", (_, arguments) => { Log(Text(arguments[0])); return DynValue.Nil; });
        script.Globals.Set("st", DynValue.NewTable(api));
        Bind(script.Globals, "wait", Wait);

        var upstream = new Table(script);
        Bind(upstream, "pause", (context, arguments) => Pause(context, arguments.SkipMethodCall()));
        Bind(upstream, "print", (_, arguments) => { Log(Text(arguments.SkipMethodCall()[0])); return DynValue.Nil; });
        Bind(upstream, "settime", (context, arguments) => SetTime(context, arguments.SkipMethodCall()));
        Bind(upstream, "gettime", (_, _) => DynValue.NewNumber(_session[View].Epoch.ToJulianDate()));
        script.Globals.Set("celestia", DynValue.NewTable(upstream));

        DynValue function = script.LoadString(source, codeFriendlyName: name);
        _coroutine = script.CreateCoroutine(function).Coroutine;
        // Check allocations between VM instructions to catch exponential string growth.
        _coroutine.AutoYieldCounter = 1;
    }

    public int View { get; }
    public bool Finished => _coroutine is null;
    public string? Error { get; private set; }

    public static LuaSessionScript Load(string path, SimulationSession session, int view, bool hasSurface, Action<string> log)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaximumSourceBytes)
            throw new InvalidDataException("Lua source exceeds the size quota.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Lua source changed during loading.");
        ReadOnlySpan<byte> content = bytes;
        if (content.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            content = content[3..];
        try
        {
            return new(StrictUtf8.GetString(content), Path.GetFileName(path), session, view, hasSurface, log);
        }
        catch (InterpreterException error)
        {
            throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error);
        }
    }

    public void Stop() => _coroutine = null;

    public bool Step(double elapsed)
    {
        _changed = false;
        if (_coroutine is not { } coroutine)
            return false;
        if (View >= _session.ViewCount)
        {
            Fail("The script's view has been closed.");
            return false;
        }
        _waitRemaining = Math.Max(0, _waitRemaining - elapsed);
        if (_waitRemaining > 0)
            return false;

        try
        {
            for (int step = 0; step < StepsPerFrame; step++)
            {
                if (_steps++ >= MaximumSteps)
                {
                    Fail("Lua instruction quota exceeded.");
                    break;
                }
                long before = GC.GetAllocatedBytesForCurrentThread();
                coroutine.Resume();
                _allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if (_allocated > MaximumAllocatedBytes)
                {
                    Fail("Lua allocation quota exceeded.");
                    break;
                }
                if (coroutine.State == CoroutineState.Dead)
                {
                    Stop();
                    break;
                }
                if (_waitRemaining > 0)
                    break;
            }
        }
        catch (InterpreterException error)
        {
            Fail(error.DecoratedMessage ?? error.Message);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            Fail(error.Message);
        }
        return _changed;
    }

    private DynValue Pause(ScriptExecutionContext context, CallbackArguments arguments)
    {
        bool paused = arguments[0].IsNil() ? _session[View].Playing : Boolean(arguments[0]);
        return Change(() => _session.SetPlaying(View, !paused));
    }

    private DynValue SetTime(ScriptExecutionContext context, CallbackArguments arguments) =>
        Change(() => _session.Seek(View, JulianInstant.FromJulianDate(Number(arguments[0]), TimeScale.Tdb)));

    private DynValue SetMode(ScriptExecutionContext context, CallbackArguments arguments)
    {
        if (!Enum.TryParse(Text(arguments[0]), ignoreCase: true, out CameraMode mode) || !Enum.IsDefined(mode))
            throw new ScriptRuntimeException("Unknown observer mode.");
        if (mode == CameraMode.Surface && !_hasSurface)
            throw new ScriptRuntimeException("The Earth surface textures are missing.");
        return Change(() => _session.SetMode(View, mode));
    }

    private DynValue Wait(ScriptExecutionContext context, CallbackArguments arguments)
    {
        double seconds = Number(arguments[0]);
        if (seconds < 0 || _totalWait + seconds > MaximumWaitSeconds)
            throw new ScriptRuntimeException("Lua wait quota exceeded.");
        _totalWait += seconds;
        _waitRemaining = seconds;
        return DynValue.NewYieldReq([]);
    }

    private DynValue Change(Action action)
    {
        action();
        _changed = true;
        return DynValue.Nil;
    }

    private void Log(string text)
    {
        if (_messages++ >= 128)
            throw new ScriptRuntimeException("Lua log quota exceeded.");
        _log(text.Length > 512 ? text[..512] : text);
    }
    private void Fail(string error) { Error = error; Stop(); }
    private static void Bind(Table table, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> callback) =>
        table.Set(name, DynValue.NewCallback((context, arguments) => callback(context, arguments)));
    private static double Number(DynValue value) => value.Type == DataType.Number && double.IsFinite(value.Number)
        ? value.Number : throw new ScriptRuntimeException("Expected a finite number.");
    private static bool Boolean(DynValue value) => value.Type == DataType.Boolean
        ? value.Boolean : throw new ScriptRuntimeException("Expected a boolean.");
    private static string Text(DynValue value) => value.Type == DataType.String
        ? value.String : throw new ScriptRuntimeException("Expected a string.");
}
