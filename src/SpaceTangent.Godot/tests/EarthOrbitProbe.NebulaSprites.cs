// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredNebulaSprites(SolarSystemScene scene,string directory)
    {
        const string variable="SPACETANGENT_DSC_ROOT";
        string? previous=System.Environment.GetEnvironmentVariable(variable);
        string saved=Path.Combine(directory,"before-nebula.stscene");
        if(!scene.SaveScenario(saved)) throw new IOException("Could not save the observers before nebula acceptance.");
        string root=Path.Combine(directory,"authored-nebula"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"witness.dsc"),"""
            Nebula "Authored Sprite Witness" { RA 1 Dec 20 Distance 10 Radius 0.5 Mesh "points.gltf" }
            """);
        using(var binary=new MemoryStream())
        {
            using(var writer=new BinaryWriter(binary,System.Text.Encoding.UTF8,true))
            {
                foreach(float value in new[]{-.5f,0,0,.5f,0,0,.4f,.4f}) writer.Write(value);
            }
            File.WriteAllText(Path.Combine(root,"points.gltf"),"""
                {"asset":{"version":"2.0"},"nodes":[{"mesh":0}],
                 "buffers":[{"uri":"data:application/octet-stream;base64,DATA","byteLength":32}],
                 "bufferViews":[{"buffer":0,"byteLength":24},{"buffer":0,"byteOffset":24,"byteLength":8}],
                 "accessors":[{"bufferView":0,"componentType":5126,"type":"VEC3","count":2},
                              {"bufferView":1,"componentType":5126,"type":"SCALAR","count":2}],
                 "meshes":[{"primitives":[{"mode":0,"attributes":{"POSITION":0,"_POINTSIZE":1}}]}]}
                """.Replace("base64,DATA","base64,"+Convert.ToBase64String(binary.ToArray()),StringComparison.Ordinal));
        }
        scene.SetProcess(false);
        try
        {
            System.Environment.SetEnvironmentVariable(variable,root);
            if(!await scene.ReloadAddOns()) throw new InvalidOperationException("The authored nebula did not load.");
            var session=scene.Session!; var body=session.World.DeepSky.Objects.Single(item=>item.Name=="Authored Sprite Witness");
            var time=SurfaceProjection.FullMoonEpoch;
            var center=body.PositionKilometers+session.World.StellarOrigin; double range=body.RadiusKilometers*4;
            var first=new ObserverView(time,CameraMode.Solar,body.Id,false)
            {
                Navigation=new(center,range,0,0,body.Id,true){Placement=new(new(0,0,range),new(0,0,-1),new(0,1,0))},
                Sky=new(false,false,false,false){DeepSky=true}
            };
            var second=first with {Selection=null,Navigation=first.Navigation!.Value with {Target=null,Tracking=false},Sky=new(false,false,false,false)};
            string path=Path.Combine(directory,"nebula-sprites.stscene");
            ScenarioFile.Save(path,new([first,second],false,0,0,session.DeltaT));
            if(!scene.LoadScenario(path)||!await scene.ReloadAddOns()||session[0]!=first||session[1]!=second)
                throw new InvalidOperationException("Nebula cameras did not survive reload independently.");
            var renderer=scene.Renderer(0); await Drawn();
            var node=renderer.GetChildren().OfType<MeshInstance3D>().Single(mesh=>mesh.Visible&&mesh.MaterialOverride is ShaderMaterial material&&
                material.Shader.ResourcePath.EndsWith("/nebula_sprites.gdshader",StringComparison.Ordinal));
            if(node.Mesh is not ArrayMesh geometry||geometry.SurfaceGetArrayLen(0)!=8||geometry.SurfaceGetArrayIndexLen(0)!=12||
                !renderer.DeepSkySpriteVisible(body.Id)||scene.Renderer(1).DeepSkySpriteVisible(body.Id))
                throw new InvalidOperationException("Two model sprites must publish eight corners and twelve indices in only the enabled view.");
            scene.Seek(0,time);
            if(!ReferenceEquals(node.Mesh,geometry)) throw new InvalidOperationException("Repeated frames must reuse the published sprite mesh.");
            await Drawn(); using var on=renderer.GetViewport().GetTexture().GetImage(); node.Hide(); await Drawn();
            using var off=renderer.GetViewport().GetTexture().GetImage(); int changed=0;
            for(int y=0;y<on.GetHeight();y+=2)for(int x=0;x<on.GetWidth();x+=2)
                if(on.GetPixel(x,y).Luminance>off.GetPixel(x,y).Luminance+.02f) changed++;
            if(changed<20||on.SavePng(Path.Combine(directory,"authored-nebula-sprites.png"))!=Error.Ok)
                throw new InvalidOperationException($"The authored nebula sprites changed only {changed} samples.");
            GD.Print($"Authored nebula GPU: two model sprites,eight corners,twelve indices,cache reuse,independent views and {changed} changed samples.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable,previous);
            await scene.ReloadAddOns(); scene.LoadScenario(saved); scene.SetProcess(true);
        }
        async Task Drawn()
        {
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        }
    }
}
