// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LuaFactoryInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-lua-factory-input-");
    private SscScriptRequest Request(string source, IReadOnlyDictionary<string, object>? arguments = null)
    {
        string path = Path.Combine(_root.FullName, "witness.lua"); File.WriteAllText(path, source);
        return new(_root.FullName, path, "make", arguments ?? new Dictionary<string, object>());
    }

    [Theory]
    [InlineData("return nil")]
    [InlineData("return { boundingRadius=0, position=function() return 1,2,3 end }")]
    [InlineData("return { boundingRadius=1e20, position=function() return 1,2,3 end }")]
    [InlineData("return { boundingRadius=1, beginDate=2, endDate=1, position=function() return 1,2,3 end }")]
    [InlineData("return { boundingRadius=1 }")]
    public void RejectedFactoryResultsCannotInstallAnOrbit(string body)
    {
        var factory = new LuaCatalogueFactories();
        Assert.Null(factory.CreateOrbit(Request("function make(p) " + body + " end")));
    }

    [Theory]
    [InlineData("return {boundingRadius=0/0, position=function() return 1,2,3 end}")]
    [InlineData("require('../outside')")]
    [InlineData("require('missing')")]
    [InlineData("require(1)")]
    [InlineData("error('factory refusal')")]
    public void InvalidFactoryInitializationReportsAnInputFailure(string body)
    {
        Assert.Throws<InvalidDataException>(() => new LuaCatalogueFactories().CreateOrbit(Request("function make(p) " + body + " end")));
    }

    [Fact]
    public void NestedArgumentsUtf8ModuleBomAndUnavailableCallbacksRespectTheFactoryContract()
    {
        var request = Request("""
            function make(p)
                assert(p.text=='Луна' and p.enabled and p.number==3.5 and p.list[1]==7 and p.map.key=='value')
                assert(p.unknown==nil and type(p.AddonPath)=='string')
                return {boundingRadius=10, position=function(self,t) return 1,2,3 end}
            end
            """, new Dictionary<string, object> { ["text"]="Луна", ["enabled"]=true, ["number"]=3.5,
                ["list"]=new object[] {7d}, ["map"]=new Dictionary<string,object> { ["key"]="value" }, ["unknown"]=new object() });
        File.WriteAllText(request.ModulePath, File.ReadAllText(request.ModulePath), new System.Text.UTF8Encoding(true));
        var factory = new LuaCatalogueFactories(); var orbit = Assert.IsType<SscScriptOrbit>(factory.CreateOrbit(request));
        Assert.Equal(new Vector3d(1,2,3), orbit.Position(new(2451545,0,TimeScale.Tdb)));
        Assert.Null(factory.CreateOrbit(request with { Function="notDefined" }));
    }

    [Fact]
    public void CallbackErrorsAndExcessivePositionsReturnUnavailableRatherThanEscapingTheEvaluator()
    {
        var request = Request("""
            function make(p) return {boundingRadius=10, position=function(self,t)
                if t==2451545 then error('no state') end
                return 1e30,0,0
            end} end
            """);
        var orbit = Assert.IsType<SscScriptOrbit>(new LuaCatalogueFactories().CreateOrbit(request));
        for (int i=0;i<7;i++) Assert.Null(orbit.Position(new(2451545+i,0,TimeScale.Tdb)));
        Assert.Null(orbit.Position(new(2451545,0,TimeScale.Tdb)));
    }

    [Fact]
    public void ModulesOutsideTheirDeclaredAddonRootAreRejectedBeforeExecution()
    {
        var request = Request("function make(p) return {} end");
        string child = Directory.CreateDirectory(Path.Combine(_root.FullName,"child")).FullName;
        Assert.Throws<InvalidDataException>(() => new LuaCatalogueFactories().CreateOrbit(request with { Root=child }));
    }

    public void Dispose() => _root.Delete(recursive:true);
}
