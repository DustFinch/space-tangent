// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using MoonSharp.Interpreter;
using static SpaceTangent.Application.LuaPatternMachine;

namespace SpaceTangent.Application;

/// <summary>A bounded matcher for the ordinary ASCII parsing subset of Lua patterns.</summary>
/// <remarks>Unsupported patterns retain the conservative native guard. Ordered NFA simulation preserves Lua's first
/// match, greedy/lazy priority and captures while bounding unsuccessful unanchored searches.</remarks>
internal sealed class LuaLinearPattern(Instruction[] program, bool anchored, bool[] positionCaptures, long workLimit)
{
    internal bool Anchored => anchored;

    internal static LuaLinearPattern? Create(string pattern, long workLimit = 4_000_000)
    {
        if (pattern.Length > 256 || pattern.Any(c => c > 127)) return null;
        var program = new List<Instruction> { new(Op.Save, 0) };
        var captures = new List<bool>();
        var atoms = new List<(bool[] Set, bool Repeat)>();
        var groups = new Stack<int>();
        bool anchored = pattern.StartsWith('^');
        for (int index = anchored ? 1 : 0; index < pattern.Length; index++)
        {
            char value = pattern[index];
            if (value == '(')
            {
                if (captures.Count == 32) return null;
                groups.Push(captures.Count);
                captures.Add(index + 1 < pattern.Length && pattern[index + 1] == ')');
                program.Add(new(Op.Save, 2 * groups.Peek() + 2));
                continue;
            }
            if (value == ')')
            {
                if (!groups.TryPop(out int group)) return null;
                program.Add(new(Op.Save, 2 * group + 3));
                continue;
            }
            if (value == '$' && index == pattern.Length - 1) { program.Add(new(Op.End)); continue; }
            bool[]? set;
            if (value == '[')
            {
                set = ReadSet(pattern, ref index);
                if (set is null) return null;
            }
            else if (value == '%')
            {
                if (++index == pattern.Length) return null;
                set = Class(pattern[index]);
                if (set is null) return null;
            }
            else
            {
                set = new bool[128];
                if (value == '.') Array.Fill(set, true);
                else set[value] = true;
            }
            bool repeat = index + 1 < pattern.Length && pattern[index + 1] is '*' or '+' or '?' or '-';
            char quantifier = repeat ? pattern[++index] : '\0';
            atoms.Add((set, repeat));
            int pc = program.Count;
            switch (quantifier)
            {
                case '*':
                case '-':
                    program.Add(quantifier == '*' ? new(Op.Split, pc + 1, pc + 3) : new(Op.Split, pc + 3, pc + 1));
                    program.Add(new(Op.Character, Set: set));
                    program.Add(new(Op.Jump, pc));
                    break;
                case '+':
                    program.Add(new(Op.Character, Set: set));
                    program.Add(new(Op.Split, pc, pc + 2));
                    break;
                case '?':
                    program.Add(new(Op.Split, pc + 1, pc + 2));
                    program.Add(new(Op.Character, Set: set));
                    break;
                default:
                    program.Add(new(Op.Character, Set: set));
                    break;
            }
        }
        if (groups.Count != 0) return null;
        // Keep the established accepted subset. MoonSharp itself refuses some overlapping repeats as too complex;
        // broadening this boundary is separate from fixing the wrong answers of previously accepted patterns.
        for (int index = 0; index + 1 < atoms.Count; index++)
            if (atoms[index].Repeat && atoms[index].Set.Where((present, c) => present && atoms[index + 1].Set[c]).Any())
                return null;
        program.Add(new(Op.Save, 1));
        program.Add(new(Op.Match));
        return new(program.ToArray(), anchored, captures.ToArray(), workLimit);
    }

    internal LuaPatternMachine Begin(string subject, LuaPatternBudget? budget = null) =>
        new(program, subject, 2 * positionCaptures.Length + 2, anchored, budget ?? new(workLimit));

    internal DynValue FindCallback(string subject)
    {
        var operation = Begin(subject);
        return DynValue.NewCallback((_, args) => FindValue(subject, args, "find", operation));
    }

    internal DynValue Call(string subject, CallbackArguments args, string name)
    {
        var operation = Begin(subject);
        if (name == "gmatch")
        {
            int cursor = 0;
            return DynValue.NewCallback((_, _) =>
            {
                Match? found = operation.Find(cursor);
                if (found is null) return DynValue.Nil;
                cursor = Math.Max(found.End, found.Start + 1);
                return Captures(subject, found, includeIndices: false);
            });
        }
        return FindValue(subject, args, name, operation);
    }

    private DynValue FindValue(string subject, CallbackArguments args, string name, LuaPatternMachine operation)
    {
        Match? match = operation.Find(Start(args[2], subject.Length));
        return match is null ? DynValue.Nil : Captures(subject, match, includeIndices: name == "find");
    }

    internal DynValue Substitute(string subject, string replacement, DynValue limit)
    {
        var operation = Begin(subject);
        double requested = limit.IsNil() ? subject.Length + 1d : limit.CastToNumber() ??
            throw new ScriptRuntimeException("bad argument #4 to 'gsub' (number expected)");
        if (double.IsNaN(requested)) throw new ScriptRuntimeException("invalid substitution limit");
        int maximum = (int)Math.Clamp(Math.Floor(requested), 0, subject.Length + 1d);
        var output = new StringBuilder();
        int cursor = 0, count = 0;
        while (cursor <= subject.Length && count < maximum)
        {
            Match? match = operation.Find(cursor);
            if (match is null) break;
            output.Append(subject, cursor, match.Start - cursor);
            for (int index = 0; index < replacement.Length; index++)
            {
                char value = replacement[index];
                if (value != '%') { output.Append(value); continue; }
                if (++index == replacement.Length) throw new ScriptRuntimeException("invalid replacement escape");
                char code = replacement[index];
                if (code == '%') output.Append('%');
                else if (code == '0' || code == '1' && positionCaptures.Length == 0)
                    output.Append(subject, match.Start, match.End - match.Start);
                else if (code is >= '1' and <= '9' && code - '0' <= positionCaptures.Length)
                {
                    int group = code - '0';
                    int first = match.Positions[2 * group], last = match.Positions[2 * group + 1];
                    if (positionCaptures[group - 1]) output.Append((first + 1).ToString(CultureInfo.InvariantCulture));
                    else output.Append(subject, first, last - first);
                }
                else throw new ScriptRuntimeException("invalid capture or replacement escape");
            }
            count++;
            cursor = match.End;
            if (match.End == match.Start)
            {
                if (cursor < subject.Length) output.Append(subject[cursor]);
                cursor++;
            }
            if (anchored) break;
        }
        cursor = Math.Min(cursor, subject.Length);
        output.Append(subject, cursor, subject.Length - cursor);
        return DynValue.NewTuple(DynValue.NewString(output.ToString()), DynValue.NewNumber(count));
    }

    private DynValue Captures(string subject, Match match, bool includeIndices)
    {
        var values = new List<DynValue>();
        if (includeIndices) { values.Add(DynValue.NewNumber(match.Start + 1)); values.Add(DynValue.NewNumber(match.End)); }
        for (int index = 0; index < positionCaptures.Length; index++)
        {
            int first = match.Positions[2 * index + 2], last = match.Positions[2 * index + 3];
            values.Add(positionCaptures[index] ? DynValue.NewNumber(first + 1) : DynValue.NewString(subject[first..last]));
        }
        if (values.Count == 0) return DynValue.NewString(subject[match.Start..match.End]);
        return values.Count == 1 ? values[0] : DynValue.NewTuple(values.ToArray());
    }

    private static int Start(DynValue value, int length)
    {
        if (value.IsNil()) return 0;
        double requested = value.CastToNumber() ?? throw new ScriptRuntimeException("match start must be a number");
        if (!double.IsFinite(requested)) throw new ScriptRuntimeException("match start must be finite");
        double index = Math.Truncate(requested);
        if (index < 0) index += length + 1d;
        // MoonSharp clamps an explicit start past the end to the final empty position.
        return (int)Math.Clamp(index - 1, 0, length);
    }

    private static bool[]? Class(char code)
    {
        char lower = char.ToLowerInvariant(code);
        if (!"acdlpsuwxz".Contains(lower))
        {
            if (char.IsLetterOrDigit(code)) return null; // Backreferences, balanced/frontier and unknown classes stay native.
            var literal = new bool[128]; literal[code] = true; return literal;
        }
        var set = new bool[128];
        for (int index = 0; index < set.Length; index++)
        {
            char c = (char)index;
            bool matches = lower switch
            {
                'a' => char.IsAsciiLetter(c), 'c' => char.IsControl(c), 'd' => char.IsAsciiDigit(c),
                'l' => c is >= 'a' and <= 'z', 'p' => char.IsPunctuation(c),
                's' => char.IsWhiteSpace(c), 'u' => c is >= 'A' and <= 'Z', 'w' => char.IsAsciiLetterOrDigit(c),
                'x' => char.IsAsciiHexDigit(c), 'z' => c == 0, _ => false
            };
            set[index] = char.IsUpper(code) ? !matches : matches;
        }
        return set;
    }

    private static bool[]? ReadSet(string pattern, ref int index)
    {
        var set = new bool[128];
        bool negate = index + 1 < pattern.Length && pattern[index + 1] == '^';
        if (negate) index++;
        bool any = false;
        while (++index < pattern.Length && pattern[index] != ']')
        {
            any = true;
            char c = pattern[index];
            if (c == '%')
            {
                if (++index == pattern.Length || Class(pattern[index]) is not { } part) return null;
                for (int i = 0; i < set.Length; i++) set[i] |= part[i];
            }
            else if (index + 2 < pattern.Length && pattern[index + 1] == '-' && pattern[index + 2] != ']')
            {
                char last = pattern[index + 2];
                if (last < c) return null;
                for (int i = c; i <= last; i++) set[i] = true;
                index += 2;
            }
            else set[c] = true;
        }
        if (!any || index == pattern.Length) return null;
        if (negate) for (int i = 0; i < set.Length; i++) set[i] = !set[i];
        return set;
    }

}
