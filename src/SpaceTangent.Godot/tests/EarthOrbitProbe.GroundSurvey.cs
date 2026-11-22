// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredGroundSurvey(SolarSystemScene scene,string directory)
    {
        var session=scene.Session!; var before=session.CaptureScenario(0);
        string saved=Path.Combine(directory,"before-ground-survey.stscene"); ScenarioFile.Save(saved,before);
        var direction=HealpixSkyGrid.Direction(5,5359,.5,.5);
        var site=EarthObserverCamera.Moscow.Site;
        JulianInstant? chosen=null; HorizontalCoordinates? pointing=null;
        var start=new JulianInstant(2460600,0,TimeScale.Tdb);
        for(int hour=0;hour<24;hour++)
        {
            var epoch=start.AddSeconds(hour*3600);
            var light=EarthSurfaceLighting.Sample(session.World.Ephemeris.Sample(epoch),site,session.DeltaT);
            var horizon=EarthObserving.HorizontalDirection(light,direction);
            if(horizon is {AltitudeDegrees:>20,AzimuthDegrees:not null}) {chosen=epoch;pointing=horizon;break;}
        }
        if(chosen is not { } time||pointing is not {AzimuthDegrees:{ } azimuth} horizonPoint)
            throw new InvalidOperationException("The authored HEALPix target never rose above the observer.");
        var first=new ObserverView(time,CameraMode.Surface,null,false)
        {
            EarthObserver=new(site,azimuth,horizonPoint.AltitudeDegrees),FieldOfViewDegrees=2,
            Sky=new(false,false,false,false){SurveyId="acceptance",SurveyBrightness=.8}
        };
        var second=first with {Sky=new(false,false,false,false)};
        string path=Path.Combine(directory,"ground-survey.stscene");
        try
        {
            ScenarioFile.Save(path,new([first,second],false,0,0,session.DeltaT));
            if(!scene.LoadScenario(path)||!await scene.ReloadAddOns()||session[0]!=first||session[1]!=second)
                throw new InvalidOperationException("Ground survey observers did not survive reload.");
            for(int i=0;i<5;i++) await Drawn();
            var canvas=scene.Renderer(0).SurfaceSurveyCanvas;
            if(canvas is not {HasSurvey:true,DetailPatches:>0}||scene.Renderer(1).SurfaceSurveyCanvas?.HasSurvey==true)
                throw new InvalidOperationException("The local survey must reach only the enabled ground view.");
            using var image=canvas.GetViewport().GetTexture().GetImage();
            Color red=image.GetPixel(image.GetWidth()/2,image.GetHeight()/2);
            if(red.R<.6||red.B>red.R-.3) throw new InvalidOperationException($"The known red tile did not reach the ground survey viewport: {red}.");
            if(image.SavePng(Path.Combine(directory,"authored-ground-survey.png"))!=Error.Ok) throw new IOException("Could not save the survey image.");
            var disabled=first with {Sky=first.Sky with {SurveyBrightness=0}};
            ScenarioFile.Save(path,new([disabled,second],false,0,0,session.DeltaT));
            if(!scene.LoadScenario(path)||canvas.HasSurvey||canvas.Visible)
                throw new InvalidOperationException("Zero brightness must release the ground survey frame.");
            if(!scene.LoadScenario(saved)) throw new InvalidOperationException("Could not restore the original scene.");
            GD.Print("Authored ground survey GPU: red HEALPix detail, independent disabled view, zero-brightness cleanup and reload.");
        }
        finally {scene.LoadScenario(saved);}
        async Task Drawn()
        {
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        }
    }
}
