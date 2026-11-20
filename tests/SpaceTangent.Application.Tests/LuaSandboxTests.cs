// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;
using Xunit;

namespace SpaceTangent.Application.Tests;

/// <summary>Library calls must stay interruptible and bounded under the instruction-by-instruction stepping of the host.</summary>
public sealed class LuaSandboxTests
{
    [Fact]
    public async Task LoopsInsideSubstitutionCallbacksReachTheInstructionQuota()
    {
        // MoonSharp runs a native gsub callback in a nested processor that never yields; the sandbox must not. Without
        // the fix the single Resume never returns, so the watchdog fails the test instead of hanging the run.
        var run = Task.Run(() => Assert.Throws<TimeoutException>(() =>
            Run(LuaSandbox.Create(), """string.gsub("a", "a", function() while true do end end)""", 50_000)));
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("""local s = string.rep("x", 2^27)""")]
    [InlineData("""local s = ("x"):rep(2^24, ",")""")]
    [InlineData("""local s = string.rep("x", 2^20); s = s:gsub("x", "xxxxxxxxxx")""")]
    [InlineData("""local s = string.format("%999d", 1)""")]
    public void OversizedResultsAreRefusedBeforeTheyAreBuilt(string code)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ScriptRuntimeException>(() => Run(LuaSandbox.Create(), code));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64L * 1024 * 1024);
    }

    // Expected values follow Lua 5.2, which MoonSharp implements natively for string replacements.
    [Theory]
    [InlineData("""return ("hello world"):gsub("%w+", function(w) return w:upper() end)""", "HELLO WORLD", 2)]
    [InlineData("""return ("abc"):gsub("", "-")""", "-a-b-c-", 4)]
    [InlineData("""return ("abc"):gsub("", function() return "-" end)""", "-a-b-c-", 4)]
    [InlineData("""return ("abc"):gsub("()", function(p) return p end)""", "1a2b3c4", 4)]
    [InlineData("""return ("$name is $age"):gsub("%$(%w+)", { name = "Ann", age = 3 })""", "Ann is 3", 2)]
    [InlineData("""return ("hello hello"):gsub("^hello", function() return "x" end)""", "x hello", 1)]
    [InlineData("""return ("aaa"):gsub("a", function() return "b" end, 2)""", "bba", 2)]
    [InlineData("""return ("abc"):gsub("%w", function(c) if c == "b" then return false end return c:upper() end)""", "AbC", 3)]
    [InlineData("""return ("x = 1, y = 2"):gsub("(%w+) = (%w+)", "%2 = %1")""", "1 = x, 2 = y", 2)]
    [InlineData("""return ("hello"):gsub("l+", string.upper)""", "heLLo", 1)]
    public void SubstitutionKeepsTheStandardSemantics(string code, string text, double count)
    {
        DynValue result = Run(LuaSandbox.Create(), code);
        Assert.Equal(DataType.Tuple, result.Type);
        Assert.Equal(text, result.Tuple[0].String);
        Assert.Equal(count, result.Tuple[1].Number);
    }

    [Fact]
    public void FormatAndRepeatStillWorkWithinTheirLimits()
    {
        Assert.Equal(" 1.00", Run(LuaSandbox.Create(), """return string.format("%5.2f", 1)""").String);
        Assert.Equal("a-a-a", Run(LuaSandbox.Create(), """return ("a"):rep(3, "-")""").String);
        Assert.Equal("", Run(LuaSandbox.Create(), """return ("a"):rep(0)""").String);
        Assert.Equal("x % 3.50", Run(LuaSandbox.Create(), """return string.format("%s %% %.2f", "x", 3.5)""").String);
    }

    [Theory]
    [InlineData("s", 20)]
    [InlineData("q", 9)]
    public void FormatBoundsAllConversionsBeforeAllocatingTheCombinedResult(string specifier, int count)
    {
        Script script = LuaSandbox.Create();
        script.Globals["s"] = new string('"', 262144);
        string code = $"return string.format('{string.Concat(Enumerable.Repeat("%" + specifier, count))}', {string.Join(",", Enumerable.Repeat("s", count))})";
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ScriptRuntimeException>(() => Run(script, code));
        // MoonSharp allocates its coroutine stack here (about 2 MiB); the rejected output alone would add > 8 MiB.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 3 * 1024 * 1024);
    }

    [Theory]
    [InlineData("return string.rep('', 1/0)")]
    [InlineData("return ('a'):rep(500):find('a*a*a*a*a*b')")]
    [InlineData("return ('a'):rep(10000):match('a*a*b')")]
    [InlineData("return ('a'):rep(10000):gsub('a*a*b', 'x')")]
    public void NonFiniteRepeatsAndExpensiveNativePatternsAreRejected(string code) =>
        Assert.Throws<ScriptRuntimeException>(() => Run(LuaSandbox.Create(), code));

    [Fact]
    public void OversizedTablesStopTheEstimatorBeforeBuildingThePendingStack()
    {
        Script script = LuaSandbox.Create();
        var table = new Table(script);
        for (int index = 1; index <= 100000; index++)
            table[index] = index;
        script.Globals["large"] = table;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(LuaSandbox.WithinRetainedLimit(script, [], 1024));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
    }

    [Fact]
    public void RetainedMemoryIsEstimatedThroughTablesAndUpvalues()
    {
        Script script = LuaSandbox.Create();
        Run(script, """
            local kept = {}
            function grow()
                for index = 1, 64 do kept[#kept + 1] = string.rep(tostring(index), 32768) end
            end
            """);
        Assert.True(LuaSandbox.WithinRetainedLimit(script, [], 1024 * 1024));
        Run(script, "grow()");
        // 64 strings of about 64 KiB characters each are reachable only through the upvalue of grow.
        Assert.False(LuaSandbox.WithinRetainedLimit(script, [], 1024 * 1024));
        Assert.True(LuaSandbox.WithinRetainedLimit(script, [], 64L * 1024 * 1024));
    }

    [Theory]
    [InlineData("local n=0 for line in input:gmatch('[^\\n]+') do n=n+1 end return n", 32768)]
    [InlineData("local n=0 for w in input:gmatch('%S+') do n=n+1 end return n", 32768)]
    [InlineData("return (input:gsub('%s+', ' '))", 32768)]
    [InlineData("local n=0 for f in input:gmatch('([^,\\n]*)') do n=n+1 end return n", 32768)]
    [InlineData("return input:match('^(%d+)%s+(%d+)')", 200)]
    [InlineData("return input:find('(%d+)%s+(%d+)%s+(%d+)')", 1000)]
    [InlineData("local n=0 for a,b in input:gmatch('(%d+)%s+(%d+)') do n=n+1 end return n", 32768)]
    public void ImporterSizedOrdinaryParsingMatchesTheNativeResult(string code, int length)
    {
        string text = string.Concat(Enumerable.Repeat("1 2 3 4 5 6 7 8 9\n", length / 18 + 1))[..length];
        Script bounded = LuaSandbox.Create(), native = new(CoreModules.Preset_SoftSandbox);
        bounded.Globals["input"] = native.Globals["input"] = text;
        Assert.Equal(Values(native.DoString(code)), Values(Run(bounded, code)));
    }

    [Fact]
    public void RecognizedPatternsPreserveNativeCapturesClassesAndIteration()
    {
        Script bounded = LuaSandbox.Create(), native = new(CoreModules.Preset_SoftSandbox);
        string ascii = new(Enumerable.Range(0, 128).Select(c => (char)c).ToArray());
        string[] patterns = ["", "()", "(%d+)%s+(%d+)", "([^,\n]*)", "%S+", "%s+", "a*", "a-", "a?b",
            "^a", "^", "$", "(a(b))", "()%d+()", "[%dA-F]+", "[^\n]+", "[a-c]+", "[%-]+", "%.", "%%",
                "a$", "(a*)", "(%d+)%s+(%d+)%s+(%d+)", "a*b*c", "a*b?c", "a-b-c", "a-b?c", "(()a)", "()a()"];
        patterns = patterns.Concat("acdlpsuwxz".SelectMany(c => new[] { "%" + c, "%" + char.ToUpperInvariant(c) })).ToArray();
        foreach (string pattern in patterns)
        foreach (string input in new[] { "", "aaa", "abc\na b,12 34 56", "12 34\n56 78\n", ascii })
        {
            bounded.Globals["input"] = native.Globals["input"] = input;
            bounded.Globals["pattern"] = native.Globals["pattern"] = pattern;
            foreach (string code in new[] { "return input:find(pattern)", "return input:find(pattern, 2)",
                "return input:find(pattern, -2)", "return input:match(pattern)", "return input:gsub(pattern, '[%0]')",
                "return input:gsub(pattern, '[%1]', 2)",
                "local t='' for x in input:gmatch(pattern) do t=t..tostring(x)..'|' end return t" })
                Assert.True(Values(native.DoString(code)) == Values(bounded.DoString(code)), $"Pattern {pattern}, input length {input.Length}: {code}\n" +
                    $"native {Values(native.DoString(code))}; bounded {Values(bounded.DoString(code))}");
        }
    }

    [Theory]
    [InlineData("return input:find('(%d+)%s+(%d+)')")]
    [InlineData("return input:gsub('(%d+)%s+(%d+)', '%2:%1')")]
    [InlineData("return input:gsub('(%d+)%s+(%d+)', function(a,b) return b..a end)")]
    [InlineData("local n=0 for x in input:gmatch('(%d+)%s+(%d+)') do n=n+1 end return n")]
    public async Task MissingSeparatorsDoNotCauseOverlappingQuadraticNativeSearches(string code)
    {
        await Task.Run(() =>
        {
            Script script = LuaSandbox.Create();
            script.Globals["input"] = new string('1', 32768);
            DynValue value = Run(script, code);
            Assert.True(value.IsNil() || value.Type == DataType.Tuple || value.Number == 0);
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AnchoredMissVisitsOneStartRegardlessOfTheRemainingSubject()
    {
        var pattern = LuaLinearPattern.Create("^%d+")!;
        var shortSearch = pattern.Begin("x" + new string('7', 63));
        var longSearch = pattern.Begin("x" + new string('7', 32767));
        Assert.Null(shortSearch.Find(0));
        Assert.Null(longSearch.Find(0));
        Assert.Equal(shortSearch.Budget.Steps, longSearch.Budget.Steps);
        Assert.InRange(longSearch.Budget.Steps, 1, 10);
    }

    [Theory]
    [InlineData("local n=0 for x in input:gmatch(pattern) do n=n+1 end return n")]
    [InlineData("return input:gsub(pattern, '')")]
    [InlineData("return input:gsub(pattern, function() return '' end)")]
    [InlineData("return input:gsub(pattern, {})")]
    public void IteratorsAndEveryReplacementKindShareOneOperationBudget(string code)
    {
        string pattern = string.Concat(Enumerable.Repeat("a?b?", 30));
        var first = LuaLinearPattern.Create(pattern, 1000)!.Begin(new string('x', 128));
        Assert.NotNull(first.Find(0)); // One search is allowed, but the complete iteration is not.
        Assert.InRange(first.Budget.Steps, 1, 999);
        Script script = LuaSandbox.Create(patternWorkLimit: 1000);
        script.Globals["input"] = new string('x', 128);
        script.Globals["pattern"] = pattern;
        Assert.Contains("sandbox matching work limit", Assert.Throws<ScriptRuntimeException>(() => Run(script, code)).Message);
    }

    private static string Values(DynValue result)
    {
        DynValue[] values = result.Type == DataType.Tuple ? result.Tuple : [result];
        int length = values.Length;
        while (length > 0 && values[length - 1].IsNil()) length--;
        return string.Join("|", values.Take(length).Select(value => value.Type + ":" + value.ToPrintString()));
    }

    private static DynValue Run(Script script, string code, int maximumSteps = 1_000_000)
    {
        Coroutine coroutine = script.CreateCoroutine(script.LoadString(code)).Coroutine;
        coroutine.AutoYieldCounter = 1;
        for (int step = 0; step < maximumSteps; step++)
        {
            DynValue result = coroutine.Resume();
            if (coroutine.State == CoroutineState.Dead)
                return result;
        }
        throw new TimeoutException("The script reached the instruction quota.");
    }
}
