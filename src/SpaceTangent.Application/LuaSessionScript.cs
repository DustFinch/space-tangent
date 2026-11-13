// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>Runs an explicitly opened observer script in bounded steps on its owning thread.</summary>
public sealed partial class LuaSessionScript
{
    private const int MaximumSourceBytes = 32 * 1024;
    private const int StepsPerFrame = 1024, MaximumSteps = 131072;
    private const long MaximumAllocatedBytes = 16 * 1024 * 1024;
    private const double MaximumWaitSeconds = 120;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SimulationSession _session;
    private readonly int _viewIdentity;
    private readonly Action<string> _log;
    private readonly bool _hasSurface;
    private readonly Script _vm;
    private Invocation? _foreground, _running;
    private bool _changed;

    internal sealed class Invocation(Coroutine coroutine, DynValue[] arguments)
    {
        internal Coroutine Coroutine { get; } = coroutine;
        internal DynValue[]? Arguments = arguments;
        internal int Steps, Messages, CatalogueReads, MediaRequests;
        internal long Allocated;
        internal double WaitRemaining, TotalWait, DollyRate;
        internal bool Complete, Yielded;
        internal string? Error, LastNotice;
    }

    private LuaSessionScript(string source, string name, SimulationSession session, int view, bool hasSurface, Action<string> log, bool plugin = false, Action<ScriptMediaRequest>? media = null)
    {
        _session = session;
        _viewIdentity = session.ViewIdentity(view);
        _hasSurface = hasSurface;
        _log = log;
        if (StrictUtf8.GetByteCount(source) > MaximumSourceBytes)
            throw new InvalidDataException("Lua source exceeds the size quota.");

        var script = _vm = new Script(CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators |
            CoreModules.Math | CoreModules.Bit32 | CoreModules.String);
        script.Options.DebugPrint = Log;
        script.Globals.Set("collectgarbage", DynValue.Nil);
        var api = new Table(script);
        Bind(api, "pause", Pause);
        Bind(api, "settime", SetTime);
        Bind(api, "gettime", (_, _) => DynValue.NewNumber(_session[View].Epoch.ToJulianDate()));
        Bind(api, "synchronizetime", (_, arguments) => Change(() =>
            _session.Synchronize(Boolean(arguments[0]), View)));
        Bind(api, "mode", SetMode);
        Bind(api, "select", (_, arguments) => Change(() =>
        {
            if (arguments[0].Type == DataType.Table)
                _session.SelectObject(View, LuaObjectId(arguments[0]));
            else
                _session.Select(View, new ObjectId(Text(arguments[0])));
        }));
        Bind(api, "wait", Wait);
        Bind(api, "log", (_, arguments) => { Log(Text(arguments[0])); return DynValue.Nil; });
        Bind(api, "notice", (_, arguments) => PrintNotice(arguments, native: false));
        BindTourActions(api);
        BindNativeObjects(api);
        script.Globals.Set("st", DynValue.NewTable(api));
        Bind(script.Globals, "wait", Wait);

        var upstream = new Table(script);
        Bind(upstream, "pause", (context, arguments) => Pause(context, arguments.SkipMethodCall()));
        Bind(upstream, "print", (_, arguments) => PrintNotice(arguments.SkipMethodCall(), native: true));
        Bind(upstream, "flash", (_, arguments) => PrintNotice(arguments.SkipMethodCall(), native: true));
        Bind(upstream, "settime", (context, arguments) => SetTime(context, arguments.SkipMethodCall()));
        Bind(upstream, "gettime", (_, _) => DynValue.NewNumber(_session[View].Epoch.ToJulianDate()));
        BindNativeObserver(upstream, plugin);
        if (media is not null)
            BindMedia(api, upstream, media);
        script.Globals.Set("celestia", DynValue.NewTable(upstream));

        _foreground = NewInvocation(script.LoadString(source, codeFriendlyName: name));
    }

    public int View => _session.IndexOfView(_viewIdentity);
    public bool Finished => _foreground is null;
    internal Script Interpreter => _vm;

    internal static LuaSessionScript PluginRuntime(SimulationSession session, int view, bool hasSurface, Action<string> log,
        Action<ScriptMediaRequest>? media = null)
    {
        var runtime = new LuaSessionScript("", "plugin", session, view, hasSurface, log, plugin: true, media: media);
        runtime.Stop();
        return runtime;
    }

    internal Invocation NewInvocation(DynValue function, params DynValue[] arguments)
    {
        var coroutine = _vm.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = 1;
        return new(coroutine, arguments);
    }
    public string? Error { get; private set; }

    public static LuaSessionScript Load(string path, SimulationSession session, int view, bool hasSurface, Action<string> log,
        Action<ScriptMediaRequest>? media = null)
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
            return new(StrictUtf8.GetString(content), Path.GetFileName(path), session, view, hasSurface, log, media: media);
        }
        catch (InterpreterException error)
        {
            throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error);
        }
    }

    public void Stop() => _foreground = null;

    public bool Step(double elapsed)
    {
        if (_foreground is not { } work)
            return false;
        bool changed = Step(work, elapsed, StepsPerFrame);
        if (work.Complete)
        {
            Error = work.Error;
            Stop();
        }
        return changed;
    }

    internal bool Step(Invocation work, double elapsed, int slice)
    {
        _changed = false;
        if (View < 0)
        {
            work.Error = "The script's view has been closed.";
            work.Complete = true;
            return false;
        }
        if (work.Complete)
            return false;

        work.Yielded = false;
        _running = work;
        try
        {
            double interval = Math.Clamp(elapsed, 0, work.WaitRemaining);
            if (work.DollyRate != 0 && interval > 0)
                _changed = _session.DollyScriptView(View, work.DollyRate * interval, NativeSnapshot());
            work.WaitRemaining -= interval;
            if (work.WaitRemaining > 0)
                return _changed;
            work.DollyRate = 0;
            long frameStart = work.Allocated;
            for (int step = 0; step < slice; step++)
            {
                if (work.Steps++ >= MaximumSteps + work.CatalogueReads * 192)
                    throw new ScriptRuntimeException($"Lua instruction quota exceeded ({work.Steps} instructions, {work.CatalogueReads} records).");
                long before = GC.GetAllocatedBytesForCurrentThread();
                if (work.Arguments is { } arguments)
                {
                    work.Arguments = null;
                    work.Coroutine.Resume(arguments);
                }
                else
                    work.Coroutine.Resume();
                work.Allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                if (work.Allocated > MaximumAllocatedBytes + work.CatalogueReads * 49152L)
                    throw new ScriptRuntimeException($"Lua allocation quota exceeded ({work.Allocated} bytes, {work.CatalogueReads} records).");
                if (work.Coroutine.State == CoroutineState.Dead)
                {
                    work.Complete = true;
                    break;
                }
                if (work.Yielded || work.CatalogueReads > 0 && work.Allocated - frameStart >= 512 * 1024)
                    break;
            }
        }
        catch (Exception error) when (error is InterpreterException or ArgumentException or
            InvalidOperationException or ArithmeticException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            work.Error = error is InterpreterException lua ? lua.DecoratedMessage ?? lua.Message : error.Message;
            work.Complete = true;
        }
        finally { _running = null; }
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

    private DynValue Wait(ScriptExecutionContext context, CallbackArguments arguments) => WaitFor(Number(arguments[0]));

    private DynValue WaitFor(double seconds, double dollyRate = 0)
    {
        if (_running is not { } work)
            throw new ScriptRuntimeException("This Lua callback cannot wait.");
        if (seconds < 0 || work.TotalWait + seconds > MaximumWaitSeconds)
            throw new ScriptRuntimeException("Lua wait quota exceeded.");
        work.TotalWait += seconds;
        work.WaitRemaining = seconds;
        work.DollyRate = dollyRate;
        work.Yielded = true;
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
        if (_running is not { } work || work.Messages++ >= 128)
            throw new ScriptRuntimeException("Lua log quota exceeded or unavailable for this callback.");
        _log(text.Length > 512 ? text[..512] : text);
    }
    private static void Bind(Table table, string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> callback) =>
        table.Set(name, DynValue.NewCallback((context, arguments) => callback(context, arguments)));
    private static double Number(DynValue value) => value.Type == DataType.Number && double.IsFinite(value.Number)
        ? value.Number : throw new ScriptRuntimeException("Expected a finite number.");
    private static bool Boolean(DynValue value) => value.Type == DataType.Boolean
        ? value.Boolean : throw new ScriptRuntimeException("Expected a boolean.");
    private static string Text(DynValue value) => value.Type == DataType.String
        ? value.String : throw new ScriptRuntimeException("Expected a string.");
}
