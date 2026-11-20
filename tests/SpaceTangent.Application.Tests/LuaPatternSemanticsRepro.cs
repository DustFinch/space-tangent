// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using MoonSharp.Interpreter;
using Xunit;
using Xunit.Abstractions;

namespace SpaceTangent.Application.Tests;

/// <summary>Review repro for ebf4d4b: the sandbox must return exactly what native MoonSharp returns, or refuse.</summary>
public sealed class LuaPatternSemanticsRepro(ITestOutputHelper output)
{
    // .NET RegexOptions.NonBacktracking (8.0.34 and 10.0.9) sometimes skips the leftmost match when an optional
    // element's character occurs earlier, followed by a character that does not continue the match.
    [Theory]
    [InlineData("return ('dx -y 5 -7'):match('-?%d+')")]
    [InlineData("local t='' for n in ('dx -y 5 -7'):gmatch('-?%d+') do t=t..n..';' end return t")]
    [InlineData("return ('dx -y 5 -7'):find('-?%d+')")]
    [InlineData("return ('dx -y 5 -7'):gsub('-?%d+', '<%0>')")]
    [InlineData("return ('a+b.b'):find('a?b')")]
    [InlineData("return ('-+b.c'):find('-?%l')")]
    [InlineData("return ('1-+bZ.ca'):find('%p?%a')")]
    [InlineData("return ('a+b.ab'):find('(a?)b')")]
    [InlineData("return ('x: ,y 1, 2'):match(',?%s*(%d)')")]
    [InlineData("local t='' for k in ('-a -b 3 -4'):gmatch('(-?%w)') do t=t..k..';' end return t")]
    public void LeftmostMatchIsTheNativeMatch(string code) => AssertSame(code);

    [Fact]
    public void RandomPatternsMatchNativeOrAreRefused()
    {
        var random = new Random(20261120);
        string[] atoms = ["a", "b", "c", "1", " ", ".", "%d", "%s", "%S", "%a", "%w", "%p", "%l", "%u", "[ab]", "[^a]",
            "[%d,]", "[a-c]", "%.", "%%", ",", "\n", "[^\n]", "[^,]", "%x", "-"];
        string[] quantifiers = ["", "", "", "*", "+", "-", "?"];
        const string alphabet = "abc1 2,.\n-%A_Z!~$+";
        string[] calls =
        [
            "return input:find(pattern)", "return input:find(pattern, init)", "return input:match(pattern, init)",
            "return input:gsub(pattern, '<%0>')", "return input:gsub(pattern, '#', 2)", "return input:gsub(pattern, {a='X'})",
            "return input:gsub(pattern, function(a, b) return '[' .. tostring(a) .. ',' .. tostring(b) .. ']' end)",
            "local t='' for a, b in input:gmatch(pattern) do t=t..tostring(a)..'/'..tostring(b)..'|' end return t"
        ];
        Script sandbox = LuaSandbox.Create(), native = new(CoreModules.Preset_SoftSandbox);
        var failures = new List<string>();
        int compared = 0;
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            var pattern = new StringBuilder();
            if (random.Next(5) == 0) pattern.Append('^');
            int open = 0;
            for (int atom = random.Next(1, 5); atom > 0; atom--)
            {
                if (random.Next(6) == 0) { pattern.Append('('); open++; }
                pattern.Append(atoms[random.Next(atoms.Length)]).Append(quantifiers[random.Next(quantifiers.Length)]);
                if (open > 0 && random.Next(3) == 0) { pattern.Append(')'); open--; }
                if (random.Next(12) == 0) pattern.Append("()");
            }
            pattern.Append(')', open);
            if (random.Next(6) == 0) pattern.Append('$');
            var input = new StringBuilder();
            for (int length = random.Next(0, 30); length > 0; length--)
                input.Append(alphabet[random.Next(alphabet.Length)]);
            sandbox.Globals["input"] = native.Globals["input"] = input.ToString();
            sandbox.Globals["pattern"] = native.Globals["pattern"] = pattern.ToString();
            sandbox.Globals["init"] = native.Globals["init"] = (double)random.Next(-5, 35);
            foreach (string code in calls)
            {
                string expected = Evaluate(native, code), actual = Evaluate(sandbox, code);
                // A sandbox refusal is allowed; a different answer is not.
                if (actual.StartsWith("error:", StringComparison.Ordinal) &&
                    (expected.StartsWith("error:", StringComparison.Ordinal) || actual.Contains("sandbox", StringComparison.Ordinal)))
                    continue;
                compared++;
                if (expected != actual && failures.Count < 20)
                    failures.Add($"{Quote(pattern.ToString())} on {Quote(input.ToString())}, init {sandbox.Globals["init"]}: {code}\n" +
                        $"  native  {expected}\n  sandbox {actual}");
            }
        }
        output.WriteLine($"{compared} comparisons");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void AssertSame(string code)
    {
        Script sandbox = LuaSandbox.Create(), native = new(CoreModules.Preset_SoftSandbox);
        Assert.Equal(Evaluate(native, code), Evaluate(sandbox, code));
    }

    private static string Evaluate(Script script, string code)
    {
        try
        {
            DynValue result = script.DoString(code);
            DynValue[] values = result.Type == DataType.Tuple ? result.Tuple : [result];
            int length = values.Length;
            while (length > 0 && values[length - 1].IsNil()) length--;
            return string.Join(" | ", values.Take(length).Select(value => value.Type + ":" + value.ToPrintString()));
        }
        catch (InterpreterException error) { return "error: " + (error.DecoratedMessage ?? error.Message); }
    }

    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
