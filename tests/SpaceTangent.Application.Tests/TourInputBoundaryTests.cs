// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class TourInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-tour-input-");
    private string Write(string source, string extension=".cel")
    {
        string path=Path.Combine(_root.FullName,"witness"+extension); File.WriteAllText(path,source); return path;
    }

    [Theory]
    [InlineData("{ wait { duration 1 duration 2 } }")]
    [InlineData("{ wait { duration 1 } } trailing")]
    [InlineData("{ wait duration 1 }")]
    [InlineData("{ rotate { axis [1 0] } }")]
    [InlineData("{ print { text \"unterminated }")]
    public void MalformedTourSyntaxCannotReturnAPartiallyConvertedScript(string source)
    {
        Assert.Throws<InvalidDataException>(()=>TourConverter.ConvertFile(Write(source)));
    }

    [Fact]
    public void CommandBudgetAndInvalidLuaAreRejectedExplicitly()
    {
        Assert.Throws<InvalidDataException>(()=>TourConverter.ConvertFile(Write("{"+string.Concat(Enumerable.Repeat("wait { duration 0 } ",257))+"}")));
        Assert.Throws<InvalidDataException>(()=>TourConverter.ConvertFile(Write("function broken(",".celx")));
        Assert.Throws<NotSupportedException>(()=>TourConverter.ConvertFile(Write("{}",".txt")));
    }

    [Fact]
    public void SkyFlagConversionProducesExecutableCommandsWithTheRequestedState()
    {
        var result=TourConverter.ConvertFile(Write("""
            { renderflags { set "atmospheres|comettails|boundaries|grid|eclipticgrid|galacticgrid|ecliptic" }
              labels { set "locations|openclusters|nebulae" } }
            """));
        Assert.Empty(result.Unapplied); Assert.Equal(2,result.Commands);
        var session=SharedClockTests.Create(); session.Split(); var other=session[1];
        var script=LuaSessionScript.Load(Write(result.Lua,".lua"),session,0,false,_=>{});
        for(int i=0;i<16&&!script.Finished;i++) script.Step(0);
        Assert.True(script.Finished); Assert.Null(script.Error);
        var sky=session[0].Sky;
        Assert.True(sky.ConstellationBoundaries&&sky.EquatorialGrid&&sky.EclipticGrid&&sky.GalacticGrid&&sky.EclipticLine&&sky.SurfacePlaces);
        Assert.False(sky.HideAtmospheres||sky.HideCometTails); Assert.True(sky.OpenClusterLabels); Assert.True(sky.NebulaLabels);
        Assert.Equal(other,session[1]);
    }

    [Theory]
    [InlineData("top",0,1)] [InlineData("topleft",-1,1)] [InlineData("topright",1,1)]
    public void NoticeOriginsPreserveTheirScreenQuadrant(string origin,int horizontal,int vertical)
    {
        var result=TourConverter.ConvertFile(Write("{ print { text \"Notice\" origin \""+origin+"\" duration 1 } }"));
        Assert.Contains($", {horizontal}, {vertical}, 0, 0)",result.Lua); Assert.Empty(result.Unapplied);
    }

    [Fact]
    public void UnsupportedOptionsAndCompressedTimingAreReportedRatherThanSilentlyDiscarded()
    {
        var result=TourConverter.ConvertFile(Write("{"+string.Concat(Enumerable.Repeat("wait { duration 300 } ",80))+"""
              mark { object "Sol/Earth" occludable true }
              play { filename "sound.wav" pan 0 nopause true } cls {}
              gotolonglat { upframe "observer" } singleview { view 1 } chase {} }
            """));
        foreach(string text in new[]{"timing compressed","mark occludable","play pan","play nopause","gotolonglat upframe","singleview view"})
            Assert.Contains(result.Unapplied,item=>item.Contains(text,StringComparison.Ordinal));
        Assert.Contains("st.notice(\"\", 0)",result.Lua); Assert.Contains("chase",result.Lua);
    }

    public void Dispose()=>_root.Delete(recursive:true);
}
