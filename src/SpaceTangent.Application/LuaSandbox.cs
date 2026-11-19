// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

/// <summary>Builds every Lua interpreter of the application with one bounded standard library.</summary>
/// <remarks>
/// The interpreters run as coroutines that yield after each instruction, so the quotas of their owners are checked
/// between instructions. A library function runs inside one instruction and cannot be interrupted. The wrappers below
/// therefore bound the work and memory of a single call before delegating to the native implementation:
/// <list type="bullet">
/// <item><c>string.rep</c> and <c>string.gsub</c> with a string replacement check the length of their result first.</item>
/// <item><c>string.gsub</c> with a function or table replacement runs a Lua implementation, so the replacement code
/// executes on the coroutine and remains subject to the instruction quota. MoonSharp would otherwise run it in a nested
/// processor that never yields.</item>
/// <item><c>string.format</c> accepts at most two-digit widths and precisions, as standard Lua does, and refuses tables
/// with metatables, whose <c>__tostring</c> would run Lua code that cannot yield.</item>
/// <item>Native pattern matching has a conservative work budget, including backtracking and repeated searches.</item>
/// </list>
/// </remarks>
internal static class LuaSandbox
{
    /// <summary>The longest string one library call may produce, in UTF-16 code units.</summary>
    internal const int MaximumStringLength = 4 * 1024 * 1024;

    /// <summary>The longest subject a pattern with repetition may scan.</summary>
    internal const int MaximumPatternSubject = 256 * 1024;

    private const int MaximumHeapNodes = 2_000_000;

    private const CoreModules Modules = CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators |
        CoreModules.Math | CoreModules.Bit32 | CoreModules.String;

    // Runs on the coroutine: find, the replacement and every concatenation are separate instructions.
    private const string ScriptedSubstitution = """
        local find, sub, select, type, tostring, error = string.find, string.sub, select, type, tostring, error
        return function(s, pattern, repl, n)
            local anchored = sub(pattern, 1, 1) == "^"
            local output, count, init, length = "", 0, 1, #s
            local function replace(first, last, ...)
                if not first then
                    return nil
                end
                local whole = sub(s, first, last)
                -- Captures are strings or positions, never nil. MoonSharp's find can return a trailing nil when the
                -- pattern has no captures, so the first capture, not the count, tells whether there are any.
                local capture = ...
                local value
                if type(repl) == "table" then
                    if capture == nil then value = repl[whole] else value = repl[capture] end
                elseif capture == nil then
                    value = repl(whole)
                else
                    value = repl(...)
                end
                if value == nil or value == false then
                    value = whole
                elseif type(value) == "number" then
                    value = tostring(value)
                elseif type(value) ~= "string" then
                    error("invalid replacement value (a " .. type(value) .. ")", 3)
                end
                return first, last, value
            end
            while init <= length + 1 and (n == nil or count < n) do
                local first, last, value = replace(find(s, pattern, init))
                if not first then
                    break
                end
                count = count + 1
                output = output .. sub(s, init, first - 1) .. value
                if last >= first then
                    init = last + 1
                else
                    output = output .. sub(s, first, first)
                    init = first + 1
                end
                if anchored then
                    break
                end
            end
            return output .. sub(s, init), count
        end
        """;

    internal static Script Create(bool metatables = false)
    {
        var script = new Script(metatables ? Modules | CoreModules.Metatables : Modules);
        script.Globals["collectgarbage"] = DynValue.Nil;
        Table strings = script.Globals.Get("string").Table;
        DynValue format = strings.Get("format"), substitute = strings.Get("gsub");
        DynValue find = strings.Get("find"), match = strings.Get("match"), iterate = strings.Get("gmatch");
        // The Lua substitution captures the native find before the wrappers replace it.
        DynValue scripted = script.Call(script.LoadString(ScriptedSubstitution, codeFriendlyName: "sandbox"));
        strings["dump"] = DynValue.Nil;
        strings["rep"] = DynValue.NewCallback((_, args) => Repeat(args), "rep");
        strings["format"] = DynValue.NewCallback((_, args) => Format(args, format), "format");
        strings["gsub"] = DynValue.NewCallback((_, args) => Substitute(args, substitute, scripted), "gsub");
        strings["find"] = Pattern(find, "find");
        strings["match"] = Pattern(match, "match");
        strings["gmatch"] = Pattern(iterate, "gmatch");
        return script;
    }

    /// <summary>
    /// Estimates the memory reachable from the interpreter's globals, its registry and extra roots, such as callbacks the
    /// host keeps. Returns false when the estimate exceeds the limit or the walk exceeds its node budget.
    /// </summary>
    internal static bool WithinRetainedLimit(Script script, IEnumerable<DynValue> roots, long limitBytes)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DynValue>();
        pending.Push(DynValue.NewTable(script.Globals));
        pending.Push(DynValue.NewTable(script.Registry));
        foreach (DynValue root in roots)
        {
            if (pending.Count >= MaximumHeapNodes)
                return false;
            pending.Push(root);
        }
        long bytes = 0;
        int nodes = 0;
        while (pending.Count > 0)
        {
            DynValue? value = pending.Pop();
            if (value is null)
                continue;
            switch (value.Type)
            {
                case DataType.String:
                    if (seen.Add(value.String))
                        bytes += 24 + 2L * value.String.Length;
                    break;
                case DataType.Table:
                    Table table = value.Table;
                    if (!seen.Add(table))
                        break;
                    bytes += 64;
                    foreach (TablePair pair in table.Pairs)
                    {
                        bytes += 48;
                        if (bytes > limitBytes || pending.Count + nodes > MaximumHeapNodes - 2)
                            return false;
                        pending.Push(pair.Key);
                        pending.Push(pair.Value);
                    }
                    if (table.MetaTable is { } meta)
                        pending.Push(DynValue.NewTable(meta));
                    break;
                case DataType.Function:
                    Closure closure = value.Function;
                    if (!seen.Add(closure))
                        break;
                    bytes += 64;
                    for (int index = 0; index < closure.GetUpvaluesCount(); index++)
                    {
                        if (pending.Count + nodes >= MaximumHeapNodes)
                            return false;
                        pending.Push(closure.GetUpvalue(index));
                    }
                    break;
                case DataType.Tuple:
                    foreach (DynValue item in value.Tuple)
                    {
                        if (pending.Count + nodes >= MaximumHeapNodes)
                            return false;
                        pending.Push(item);
                    }
                    break;
            }
            if (bytes > limitBytes || ++nodes > MaximumHeapNodes)
                return false;
        }
        return true;
    }

    private static DynValue Repeat(CallbackArguments args)
    {
        string text = Text(args[0], "rep", 1);
        if (args[1].CastToNumber() is not { } requested || !double.IsFinite(requested))
            throw new ScriptRuntimeException("bad argument #2 to 'rep' (number expected)");
        string separator = args[2].IsNil() ? "" : Text(args[2], "rep", 3);
        double count = Math.Floor(requested);
        double length = count < 1 ? 0 : text.Length * count + separator.Length * (count - 1);
        if (length == 0)
            return DynValue.NewString("");
        if (length > MaximumStringLength)
            throw new ScriptRuntimeException("string.rep would exceed the sandbox string limit.");
        var builder = new StringBuilder((int)length);
        for (int index = 0; index < (int)count; index++)
        {
            if (index > 0)
                builder.Append(separator);
            builder.Append(text);
        }
        return DynValue.NewString(builder.ToString());
    }

    private static DynValue Format(CallbackArguments args, DynValue native)
    {
        string format = Text(args[0], "format", 1);
        if (format.Length > MaximumStringLength)
            throw new ScriptRuntimeException("string.format would exceed the sandbox string limit.");
        long bound = format.Length;
        int argument = 1;
        for (int index = 0; index < format.Length; index++)
        {
            if (format[index] != '%')
                continue;
            if (++index < format.Length && format[index] == '%')
                continue;
            while (index < format.Length && "-+ #0".Contains(format[index]))
                index++;
            bool tooLong = Digits(format, ref index) > 2;
            if (!tooLong && index < format.Length && format[index] == '.')
            {
                index++;
                tooLong = Digits(format, ref index) > 2;
            }
            if (tooLong)
                throw new ScriptRuntimeException("invalid format (width or precision too long)");
            if (index >= format.Length)
                break; // Let the native formatter report the malformed conversion.
            DynValue value = args[argument++];
            if (value.Type == DataType.Table && value.Table.MetaTable is not null)
                throw new ScriptRuntimeException("string.format cannot convert a table with a metatable in this sandbox.");
            // Width/precision are at most 99. Binary64 fixed notation needs fewer than 512 characters. Quoted strings
            // can escape each UTF-16 character plus quotes; count all conversions before native code allocates anything.
            bound += format[index] switch
            {
                's' => Math.Max(99L, value.CastToString()?.Length ?? 128),
                'q' => Math.Max(99L, 4L * (value.CastToString()?.Length ?? 128) + 2),
                _ => 512
            };
            if (bound > MaximumStringLength)
                throw new ScriptRuntimeException("string.format would exceed the sandbox string limit.");
        }
        for (int index = 1; index < args.Count; index++)
            if (args[index].Type == DataType.Table && args[index].Table.MetaTable is not null)
                throw new ScriptRuntimeException("string.format cannot convert a table with a metatable in this sandbox.");
        return DynValue.NewTailCallReq(native, args.GetArray(0));

        static int Digits(string text, ref int index)
        {
            int start = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
                index++;
            return index - start;
        }
    }

    private static DynValue Substitute(CallbackArguments args, DynValue native, DynValue scripted)
    {
        string subject = Text(args[0], "gsub", 1), pattern = Text(args[1], "gsub", 2);
        CheckSubject(subject, pattern, repeatedSearch: true);
        DynValue replacement = args[2], limit = args[3].IsNil() ? DynValue.Nil : args[3];
        DynValue[] call = [DynValue.NewString(subject), DynValue.NewString(pattern), replacement, limit];
        if (replacement.Type is DataType.Function or DataType.ClrFunction or DataType.Table)
            return DynValue.NewTailCallReq(scripted, call);
        if (replacement.Type is not (DataType.String or DataType.Number))
            throw new ScriptRuntimeException("bad argument #3 to 'gsub' (string/function/table expected)");

        // Unmatched text is copied once. Each match adds the replacement, and each capture reference adds at most the
        // matched text, whose total over all matches is bounded by the subject.
        string text = replacement.CastToString();
        double maximum = limit.Type == DataType.Number && !double.IsNaN(limit.Number) ? Math.Floor(limit.Number) : double.PositiveInfinity;
        double matches = Math.Max(0, Math.Min(subject.Length + 1d, maximum));
        double bound = subject.Length + matches * text.Length + CaptureReferences(text) * (double)subject.Length;
        if (bound > MaximumStringLength)
            throw new ScriptRuntimeException("string.gsub would exceed the sandbox string limit.");
        return DynValue.NewTailCallReq(native, call);
    }

    private static DynValue Pattern(DynValue native, string name) => DynValue.NewCallback((_, args) =>
    {
        string subject = Text(args[0], name, 1), pattern = Text(args[1], name, 2);
        if (!(name == "find" && args[3].CastToBool()))
            CheckSubject(subject, pattern, repeatedSearch: name == "gmatch");
        return DynValue.NewTailCallReq(native, args.GetArray(0));
    }, name);

    private static void CheckSubject(string subject, string pattern, bool repeatedSearch = false)
    {
        // Saturating upper estimate of native work: each optional/repeated item may introduce another branch across
        // the subject; unanchored matching may try every starting position. Deliberately conservative for ambiguous
        // patterns. This is a library-call guard, not process isolation or an exact VM memory limit.
        const double maximumWork = 4_000_000;
        double length = subject.Length + 1d;
        double work = length * Math.Max(1, pattern.Length);
        bool anchored = pattern.StartsWith('^');
        bool inClass = false;
        int branches = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '%')
            {
                if (++index < pattern.Length && (pattern[index] == 'b' || char.IsAsciiDigit(pattern[index])))
                    work *= length;
            }
            else if (pattern[index] == '[' && !inClass)
                inClass = true;
            else if (pattern[index] == ']' && inClass)
                inClass = false;
            else if (!inClass && pattern[index] is '*' or '+' or '-' or '?')
            {
                if (subject.Length > MaximumPatternSubject)
                    throw new ScriptRuntimeException("A pattern with repetition cannot scan a subject this long in the sandbox.");
                if (branches++ > 0 || !anchored || repeatedSearch)
                    work *= length;
            }
            if (work > maximumWork)
                throw new ScriptRuntimeException("The pattern exceeds the sandbox native matching work limit.");
        }
        // Balanced matching and backreferences above also account for repeated scans.
        if (work > maximumWork)
            throw new ScriptRuntimeException("The pattern exceeds the sandbox native matching work limit.");
    }

    private static int CaptureReferences(string replacement)
    {
        int count = 0;
        for (int index = 0; index + 1 < replacement.Length; index++)
        {
            if (replacement[index] != '%')
                continue;
            if (char.IsAsciiDigit(replacement[index + 1]))
                count++;
            index++;
        }
        return count;
    }

    private static string Text(DynValue value, string function, int position) => value.Type switch
    {
        DataType.String => value.String,
        DataType.Number => value.CastToString(),
        _ => throw new ScriptRuntimeException(
            $"bad argument #{position} to '{function}' (string expected, got {value.Type.ToString().ToLowerInvariant()})")
    };
}
