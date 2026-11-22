// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverPluginBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-plugin-boundaries-");
    private ObserverLuaPlugin Create(string source, SimulationSession? session = null, int view = 0, Action? clear = null)
    {
        File.WriteAllText(Path.Combine(_root.FullName, "main.lua"), source);
        return new(new(_root.FullName, "witness", "Witness", "1", "main.lua", []),
            session ?? SharedClockTests.Create(), view, false, _ => { }, clearMedia: clear);
    }

    [Fact]
    public void OnlyDeclaredFilesCanBeQueuedAndSyntaxErrorsAreReportedAsInvalidInput()
    {
        var plugin = Create("return 1");
        Assert.Throws<InvalidOperationException>(() => plugin.StartFile("undeclared.lua"));
        Assert.Throws<InvalidDataException>(() => Create("local = broken"));
    }

    [Fact]
    public void SixteenConcurrentInvocationsAreBoundedAndDisabledPluginsCannotQueueMore()
    {
        var plugin = Create("st.wait(10)");
        for (int i = 1; i < 16; i++) plugin.StartFile("main.lua");
        Assert.Throws<InvalidOperationException>(() => plugin.StartFile("main.lua"));
        Assert.Throws<InvalidOperationException>(() => plugin.RunCommand("absent"));
        plugin.Disable();
        Assert.False(plugin.HasJobs); Assert.False(plugin.Enabled);
        Assert.Throws<InvalidOperationException>(() => plugin.StartFile("main.lua"));
        Assert.False(plugin.Step(1)); Assert.False(plugin.Disable());
    }

    [Theory]
    [InlineData("return 1")]
    [InlineData("return string.rep('a', 32769)")]
    [InlineData("return 'local = broken'")]
    [InlineData("error('refused witness')")]
    public void ImportersMustReturnBoundedValidLuaText(string body)
    {
        var plugin = Create("plugin.importer('.witness', function(input, name) " + body + " end, 'Witness')");
        plugin.Step(0); Assert.Null(plugin.Error);
        Assert.Throws<InvalidDataException>(() => plugin.Convert(".witness", "input", "witness.lua"));
        Assert.True(plugin.Enabled);
    }

    [Fact]
    public void ImporterInputLimitIsUtf8BytesAndUnavailableImportersReturnNoConversion()
    {
        var plugin = Create("plugin.importer('.witness', function(input, name) return 'return 1' end, 'Witness')");
        plugin.Step(0);
        Assert.Null(plugin.Convert(".unknown", "input", "name"));
        Assert.Equal("return 1", plugin.Convert(".witness", new string('a', 32768), "name"));
        Assert.Throws<InvalidDataException>(() => plugin.Convert(".witness", new string('я', 16385), "name"));
        plugin.Disable(); Assert.Null(plugin.Convert(".witness", "input", "name"));
    }

    [Theory]
    [InlineData("plugin.command('', 'Witness', function() end)", "command")]
    [InlineData("for i=1,33 do plugin.command('cmd'..i, 'Witness', function() end) end", "command")]
    [InlineData("plugin.on('unsupported', function() end)", "event")]
    [InlineData("plugin.importer('.bad#', function() end, 'Witness')", "extension")]
    [InlineData("for i=1,17 do plugin.importer('.ext'..i, function() end, 'Witness') end", "importer")]
    [InlineData("require('../outside')", "module")]
    [InlineData("plugin.on('tick', 1)", "function")]
    [InlineData("plugin.command('valid', '', function() end)", "label")]
    public void InvalidRegistrationFailsTheInvocationWithADiagnostic(string source, string expected)
    {
        var plugin = Create(source); plugin.Step(0);
        Assert.Contains(expected, plugin.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(plugin.HasJobs);
    }

    [Theory]
    [InlineData("st.wait(1)", "without waiting")]
    [InlineData("error('cleanup witness')", "cleanup witness")]
    public void CleanupMustFinishSynchronouslyAndAlwaysClearsPublishedCommands(string source, string error)
    {
        int cleared = 0;
        var plugin = Create("plugin.command('cmd','Witness',function() end); plugin.on('disable',function() " + source + " end)", clear: () => cleared++);
        plugin.Step(0); Assert.Single(plugin.Commands);
        plugin.Disable(); Assert.Contains(error, plugin.Error);
        Assert.Empty(plugin.Commands); Assert.Empty(plugin.Importers);
        Assert.False(plugin.Enabled); Assert.Equal(1, cleared);
    }

    [Fact]
    public void RemovingTheOwningObserverDisablesJobsAndClearsMedia()
    {
        var session = SharedClockTests.Create(); session.Split(); int cleared = 0;
        var plugin = Create("st.wait(10)", session, 1, () => cleared++);
        session.CloseSecondView();
        Assert.False(plugin.Step(0)); Assert.False(plugin.Enabled); Assert.False(plugin.HasJobs);
        Assert.Equal("The plugin observer was closed.", plugin.Error);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void RemovingARegisteredEventStopsFurtherInvocations()
    {
        var plugin = Create("plugin.on('tick', function() error('must not run') end); plugin.on('tick', nil)");
        plugin.Step(0); plugin.Step(1);
        Assert.Null(plugin.Error); Assert.False(plugin.HasJobs);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
