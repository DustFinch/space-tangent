// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class TourRuntimeContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-tour-runtime-");
    private LuaSessionScript Run(string source, SimulationSession session)
    {
        string path = Path.Combine(_root.FullName, "witness.lua"); File.WriteAllText(path, source);
        var script = LuaSessionScript.Load(path, session, 0, false, _ => { });
        for (int i = 0; i < 16 && !script.Finished; i++) script.Step(0);
        Assert.True(script.Finished); return script;
    }

    [Fact]
    public void ALocalTourCanEnumerateSelectAndPersistItsSkySettingsWithoutChangingTheOtherView()
    {
        var session = SharedClockTests.Create(); session.Split(); session.Synchronize(false, 0); session.Step(1, 3600);
        session.SetSkyLayers(0, session[0].Sky with { HideAtmospheres = true, HideCometTails = true });
        var other = session[1];
        var script = Run("""
            local first, again = st.cataloguetour(), st.cataloguetour()
            assert(#first == 32 and #again == 32)
            local seen = {}
            for i, id in ipairs(first) do assert(not seen[id]); seen[id]=true; assert(again[i]==id) end
            assert(st.tryselect('sol/moon'))
            assert(not st.tryselect('sol/ceres'))
            for _, layer in ipairs({'atmospheres','comettails','boundaries','grid','eclipticgrid','galacticgrid',
                'ecliptic','places','clusterlabels','nebulalabels'}) do st.sky(layer, true) end
            """, session);
        Assert.Null(script.Error);
        var sky = session[0].Sky;
        Assert.False(sky.HideAtmospheres); Assert.False(sky.HideCometTails);
        Assert.True(sky.ConstellationBoundaries && sky.EquatorialGrid && sky.EclipticGrid && sky.GalacticGrid && sky.EclipticLine && sky.SurfacePlaces);
        Assert.True(sky.OpenClusterLabels); Assert.True(sky.NebulaLabels);
        Assert.Equal(SolarSystemSample.MoonId, session[0].Selection);
        var restored = ScenarioFile.Decode(ScenarioFile.Encode(session.CaptureScenario(0)));
        Assert.Equal(sky, restored.Views[0].Sky); Assert.Equal(other, restored.Views[1]);
        Assert.Equal(session.DeltaT, restored.DeltaT);
    }

    [Theory]
    [InlineData("st.gotoobject(-1, 2)", "parameters")]
    [InlineData("st.gotoobject(1, 0)", "parameters")]
    [InlineData("st.sky('unknown-layer', true)", "sky layer")]
    [InlineData("st.legacyframe('unknown-frame', assert(st.find('Sol/Earth/Moon')))", "observer frame")]
    [InlineData("st.sky('lighttime', true)", "unavailable")]
    [InlineData("st.legacyturn(0,1,0,1e300,1e-300,false)", "rotation")]
    [InlineData("st.legacymove(1,0,0,-1,1)", "movement")]
    [InlineData("st.legacymove(1e100,0,0,1,1)", "movement")]
    [InlineData("st.legacydolly(1,-1)", "distance change")]
    [InlineData("st.legacydolly(1e300,1e-300)", "distance change")]
    public void InvalidTourRequestsReportTheirContractAndPreserveTheObserver(string source, string message)
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        session.RestoreScenario(before with { Views = [before.Views[0] with { Selection = null }] });
        byte[] unchanged = ScenarioFile.Encode(session.CaptureScenario(0));
        Assert.Contains(message, Run(source, session).Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unchanged, ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void OptionalFrameAndFlightCommandsWithoutASelectionAreNoOps()
    {
        var session = SharedClockTests.Create(); var before = session.CaptureScenario(0);
        session.RestoreScenario(before with { Views = [before.Views[0] with { Selection = null }] });
        byte[] unchanged = ScenarioFile.Encode(session.CaptureScenario(0));
        var script = Run("st.legacyframe('equatorial', nil); st.gotoobject(0, 2)", session);
        Assert.Null(script.Error); Assert.Equal(unchanged, ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Theory]
    [InlineData("unknown","Unknown observer mode")]
    [InlineData("surface","surface textures")]
    public void UnsupportedModesCannotReplaceTheCurrentObserver(string mode,string diagnostic)
    {
        var session=SharedClockTests.Create(); byte[] before=ScenarioFile.Encode(session.CaptureScenario(0));
        Assert.Contains(diagnostic,Run("st.mode('"+mode+"')",session).Error,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before,ScenarioFile.Encode(session.CaptureScenario(0)));
    }

    [Fact]
    public void AForegroundScriptCannotContinueAfterItsOwningViewCloses()
    {
        var session=SharedClockTests.Create(); session.Split();
        string path=Path.Combine(_root.FullName,"closed.lua"); File.WriteAllText(path,"st.pause(); st.wait(1)");
        var script=LuaSessionScript.Load(path,session,1,false,_=>{});
        session.RemoveView(1); Assert.False(script.Step(0)); Assert.True(script.Finished);
        Assert.Contains("view has been closed",script.Error,StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8BomAndOptionalPauseToggleRemainExecutable()
    {
        var session=SharedClockTests.Create(); bool playing=session[0].Playing;
        string path=Path.Combine(_root.FullName,"bom.lua"); File.WriteAllText(path,"st.pause(); st.mode('lunar')",new System.Text.UTF8Encoding(true));
        var script=LuaSessionScript.Load(path,session,0,false,_=>{});
        for(int i=0;i<8&&!script.Finished;i++) script.Step(0);
        Assert.True(script.Finished); Assert.Null(script.Error); Assert.NotEqual(playing,session[0].Playing);
        Assert.Equal(CameraMode.Lunar,session[0].Mode);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
