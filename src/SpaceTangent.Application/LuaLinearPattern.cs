// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

/// <summary>A bounded matcher for the ordinary ASCII parsing subset of Lua patterns.</summary>
/// <remarks>Unsupported or ambiguous patterns retain the conservative native guard. The non-backtracking engine
/// also bounds unsuccessful unanchored searches; disjoint character classes alone would not provide that guarantee.</remarks>
internal sealed class LuaLinearPattern(Regex regex, bool anchored, bool[] positionCaptures, int patternLength)
{
    internal bool Anchored => anchored;

    internal static LuaLinearPattern? Create(string pattern)
    {
        if (pattern.Length > 256 || pattern.Any(c => c > 127)) return null;
        var expression = new StringBuilder();
        var captures = new List<bool>();
        var atoms = new List<(bool[] Set, bool Repeat)>();
        int depth = 0;
        bool anchored = pattern.StartsWith('^');
        for (int index = anchored ? 1 : 0; index < pattern.Length; index++)
        {
            char value = pattern[index];
            if (value == '(')
            {
                if (++depth > 32 || captures.Count == 32) return null;
                captures.Add(index + 1 < pattern.Length && pattern[index + 1] == ')');
                expression.Append('(');
                continue;
            }
            if (value == ')')
            {
                if (--depth < 0) return null;
                expression.Append(')');
                continue;
            }
            if (value == '$' && index == pattern.Length - 1) { expression.Append("\\z"); continue; }
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
            string? atom = SetExpression(set);
            if (atom is null) return null;
            expression.Append(atom);
            bool repeat = index + 1 < pattern.Length && pattern[index + 1] is '*' or '+' or '?' or '-';
            if (repeat)
            {
                char quantifier = pattern[++index];
                expression.Append(quantifier == '-' ? "*?" : quantifier.ToString());
            }
            atoms.Add((set, repeat));
        }
        if (depth != 0) return null;
        for (int index = 0; index + 1 < atoms.Count; index++)
            if (atoms[index].Repeat && atoms[index].Set.Where((present, c) => present && atoms[index + 1].Set[c]).Any())
                return null;
        try
        {
            return new(new Regex(expression.ToString(), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)), anchored, captures.ToArray(), pattern.Length);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }

    internal void Check(string subject)
    {
        if ((subject.Length + 1L) * Math.Max(1, patternLength) > 4_000_000)
            throw new ScriptRuntimeException("The pattern exceeds the sandbox matching work limit.");
    }

    internal DynValue Call(string subject, CallbackArguments args, string name)
    {
        Check(subject);
        if (name == "gmatch")
        {
            int cursor = 0;
            return DynValue.NewCallback((_, _) =>
            {
                Match? found = Find(subject, cursor);
                if (found is null) return DynValue.Nil;
                cursor = found.Index + Math.Max(1, found.Length);
                return Captures(found, includeIndices: false);
            });
        }
        int start = Start(args[2], subject.Length);
        Match? match = Find(subject, start);
        return match is null ? DynValue.Nil : Captures(match, includeIndices: name == "find");
    }

    internal DynValue Substitute(string subject, string replacement, DynValue limit)
    {
        Check(subject);
        double requested = limit.IsNil() ? subject.Length + 1d : limit.CastToNumber() ??
            throw new ScriptRuntimeException("bad argument #4 to 'gsub' (number expected)");
        if (double.IsNaN(requested)) throw new ScriptRuntimeException("invalid substitution limit");
        int maximum = (int)Math.Clamp(Math.Floor(requested), 0, subject.Length + 1d);
        var output = new StringBuilder();
        int cursor = 0, count = 0;
        while (cursor <= subject.Length && count < maximum)
        {
            Match? match = Find(subject, cursor);
            if (match is null) break;
            output.Append(subject, cursor, match.Index - cursor);
            for (int index = 0; index < replacement.Length; index++)
            {
                char value = replacement[index];
                if (value != '%') { output.Append(value); continue; }
                if (++index == replacement.Length) throw new ScriptRuntimeException("invalid replacement escape");
                char code = replacement[index];
                if (code == '%') output.Append('%');
                else if (code == '0' || code == '1' && positionCaptures.Length == 0) output.Append(match.Value);
                else if (code is >= '1' and <= '9' && code - '0' <= positionCaptures.Length)
                {
                    int group = code - '0';
                    output.Append(positionCaptures[group - 1]
                        ? (match.Groups[group].Index + 1).ToString(CultureInfo.InvariantCulture) : match.Groups[group].Value);
                }
                else throw new ScriptRuntimeException("invalid capture or replacement escape");
            }
            count++;
            cursor = match.Index + match.Length;
            if (match.Length == 0)
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

    private Match? Find(string subject, int start)
    {
        if (start > subject.Length) return null;
        try
        {
            Match match = regex.Match(subject, Math.Max(0, start));
            return match.Success && (!anchored || match.Index == start) ? match : null;
        }
        catch (RegexMatchTimeoutException) { throw new ScriptRuntimeException("The pattern exceeded its matching time budget."); }
    }

    private DynValue Captures(Match match, bool includeIndices)
    {
        var values = new List<DynValue>();
        if (includeIndices) { values.Add(DynValue.NewNumber(match.Index + 1)); values.Add(DynValue.NewNumber(match.Index + match.Length)); }
        for (int index = 0; index < positionCaptures.Length; index++)
            values.Add(positionCaptures[index] ? DynValue.NewNumber(match.Groups[index + 1].Index + 1)
                : DynValue.NewString(match.Groups[index + 1].Value));
        if (values.Count == 0) return DynValue.NewString(match.Value);
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

    private static string? SetExpression(bool[] set)
    {
        var text = new StringBuilder("[");
        for (int index = 0; index < set.Length; index++)
        {
            if (!set[index]) continue;
            int first = index;
            while (index + 1 < set.Length && set[index + 1]) index++;
            text.Append($"\\u{first:x4}");
            if (index != first) text.Append($"-\\u{index:x4}");
        }
        return text.Length == 1 ? null : text.Append(']').ToString();
    }
}
