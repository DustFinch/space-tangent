// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Tests;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredArchiveLayers(SolarSystemScene scene,string directory)
    {
        string saved=Path.Combine(directory,"before-archive-layers.stscene");
        if(!scene.SaveScenario(saved)) throw new IOException("Could not retain the original observers.");
        // New analytical wire inputs, not recovered legacy recordings.
        var fixture=new LegacyWireFixture().Names("Sol/Venus","surface","Sol/Mars","Sol/Saturn/Titan",
            "Uncatalogued star","star/o","star/b","star/a","star/f","star/g","star/k","star/m","Label");
        foreach(uint name in new uint[]{0,2,3}) fixture.Record(3,Frame(name,1,true));
        foreach(uint material in Enumerable.Range(5,7).Select(i=>(uint)i)) fixture.Record(3,Frame(4,material,false));
        byte[] labels=Frame(0,1,true,Labels()); H(labels,108,10); fixture.Record(3,labels);
        fixture.Record(3,Frame(0,1,true));
        string path=Path.Combine(directory,"authored-archive-layers.celframe"); File.WriteAllBytes(path,fixture.File());
        scene.SetProcess(false);
        try
        {
            if(!scene.StartReplay(path)) throw new InvalidOperationException("Could not open the authored archive layers: "+
                ((Label)scene.FindChild("WorkspaceStatus",recursive:true,owned:false)).Text);
            var renderer=scene.Renderer(0);
            foreach(var id in new[]{SolarSystemSample.VenusId,PlanetCatalogue.MarsId,NaturalBodyCatalogue.Bodies.Single(body=>body.NaifId==606).Id})
            {
                await Drawn();
                var shell=renderer.ArchivedAtmosphere(id);
                if(shell is not {Visible:true,MaterialOverride:ShaderMaterial material} ||
                    Math.Abs(material.GetShaderParameter("ground_radius_km").AsSingle()-15)>1e-5 ||
                    Math.Abs(material.GetShaderParameter("shell_height_km").AsSingle()-3)>1e-5)
                    throw new InvalidOperationException($"The recorded {id.Value} shell did not retain its physical dimensions.");
                if(id==SolarSystemSample.VenusId && material.NextPass is not ShaderMaterial)
                    throw new InvalidOperationException("Venus requires its distinct transmission and scattering passes.");
                scene._Process(1d/30);
            }
            Color? hot=null,cool=null;
            for(int i=0;i<7;i++)
            {
                await Drawn();
                if(renderer.ArchivedBody("Uncatalogued star") is not {Visible:true,MaterialOverride:ShaderMaterial material} ||
                    !material.GetShaderParameter("emissive").AsBool() || !material.GetShaderParameter("tint_map").AsBool())
                    throw new InvalidOperationException("Every recorded stellar class requires an emissive tinted surface.");
                Color tint=material.GetShaderParameter("tint").AsColor();
                if(i==0) hot=tint; if(i==6) cool=tint;
                scene._Process(1d/30);
            }
            if(hot is not { } blue || cool is not { } red || blue.B<=blue.R || red.R<=red.B)
                throw new InvalidOperationException("O and M surfaces must retain their blue and red spectral tints.");
            await Drawn();
            if(renderer.ArchivedLabels.Count!=10 || renderer.ArchivedSymbols.Count!=2 ||
                renderer.ArchivedSymbols.Count(symbol=>symbol.Square)!=1 ||
                renderer.ArchivedSymbols.Any(symbol=>symbol.Diameter<=0))
                throw new InvalidOperationException("Recorded open cluster circles and nebula squares must coexist with label kinds.");
            using var before=renderer.GetViewport().GetTexture().GetImage();
            scene._Process(1d/30); await Drawn();
            if(renderer.ArchivedSymbols.Count!=0 || renderer.ArchivedLabels.Any(label=>label.Visible))
                throw new InvalidOperationException("A following frame without labels must hide the entire previous overlay pool.");
            using var after=renderer.GetViewport().GetTexture().GetImage(); int changed=0;
            for(int y=0;y<before.GetHeight();y+=2) for(int x=0;x<before.GetWidth();x+=2)
                if(Math.Abs(before.GetPixel(x,y).Luminance-after.GetPixel(x,y).Luminance)>.02f) changed++;
            if(changed<20 || before.SavePng(Path.Combine(directory,"authored-archive-layers.png"))!=Error.Ok)
                throw new InvalidOperationException($"Archived labels and symbols changed only {changed} samples.");
            if(!scene.LoadScenario(saved) || renderer.ArchivedAtmosphere(SolarSystemSample.VenusId)?.Visible==true)
                throw new InvalidOperationException("The recorded world shell persisted into the live scene.");
            GD.Print($"Authored archive layers GPU: Venus/Mars/Titan shells, seven spectral classes, ten label kinds, two symbols, {changed} changed samples and cleanup.");
        }
        finally {scene.SetProcess(true);}

        byte[] Frame(uint name,uint material,bool atmosphere,byte[]? overlays=null)
        {
            byte[] body=fixture.Body(); U(body,0,name); U(body,48,material); F(body,16,15); F(body,60,atmosphere?18:0);
            F(body,40,0); F(body,44,1); return fixture.Frame(body,overlays);
        }
        byte[] Labels()
        {
            using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
            for(byte kind=0;kind<=9;kind++)
            {
                writer.Write(0u); writer.Write(12u);
                writer.Write(-40f+kind*8); writer.Write(20f); writer.Write(-100f);
                writer.Write(kind); writer.Write(1f); writer.Write((byte)0); writer.Write(.04f);
            }
            writer.Write((byte)0); writer.Write((byte)0); return stream.ToArray();
        }
        async Task Drawn()
        {
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        }
    }
}
