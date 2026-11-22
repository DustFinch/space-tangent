// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredMissionTiles(SolarSystemScene scene,string directory)
    {
        var source=LocalSurfaceMaps.Load(System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_TILES")!)
            .Single(maps=>maps.Name=="Mars");
        if(source.Color?.Tiles.Count!=170||source.Normal?.Tiles.Count!=170)
            throw new InvalidOperationException("The original Mars fixture must retain both 170-tile pyramids.");
        string root=Path.Combine(directory,"authored-mission-tiles"); Directory.CreateDirectory(root);
        var color=CopyMap(source.Color,"color"); var normal=CopyMap(source.Normal,"normal");
        File.WriteAllText(Path.Combine(root,"globe.json"),JsonSerializer.Serialize(new
        {
            version="1.0",items=new[]{new
            {
                name="Authored Tiled Globe",center="Earth",
                trajectory=new {type="FixedPoint",position=new[]{1e8,0d,0d}},
                geometry=new {type="Globe",radius=3396.19,baseMap=color,normalMap=normal}
            }}
        }));
        const string variable="SPACETANGENT_SAMPLED_MISSIONS_ROOT"; string? previous=System.Environment.GetEnvironmentVariable(variable);
        string saved=Path.Combine(directory,"before-mission-local-tiles.stscene");
        if(!scene.SaveScenario(saved)) throw new IOException("Could not preserve the observers before mission tiles.");
        try
        {
            System.Environment.SetEnvironmentVariable(variable,root);
            if(!await scene.ReloadAddOns()) throw new InvalidOperationException("The authored globe with original DDS maps did not load.");
            var session=scene.Session!; var id=new ObjectId("mission/authored-tiled-globe");
            var time=SurfaceProjection.FullMoonEpoch; var first=View(time); var second=View(time.AddSeconds(18*86400));
            string path=Path.Combine(directory,"mission-local-tiles.stscene"); ScenarioFile.Save(path,new([first,second],false,0,0,session.DeltaT));
            if(!scene.LoadScenario(path)||!await scene.ReloadAddOns()||session[0]!=first||session[1]!=second)
                throw new InvalidOperationException("The tiled globe lost an independent camera during reload.");
            ShaderMaterial[] materials=Enumerable.Range(0,2).Select(index=>scene.Renderer(index).ImportedBodyNode(id)?.MaterialOverride as ShaderMaterial
                ??throw new InvalidOperationException("The tiled mission material is absent.")).ToArray();
            if(ReferenceEquals(materials[0],materials[1])||
                materials[0].GetShaderParameter("light_in_body").AsVector3()==materials[1].GetShaderParameter("light_in_body").AsVector3()||
                materials[0].GetShaderParameter("native_base_pages").AsGodotObject()!=materials[1].GetShaderParameter("native_base_pages").AsGodotObject())
                throw new InvalidOperationException("Mission tile atlases must be shared while per-view lighting stays independent.");
            using var pages=(materials[0].GetShaderParameter("native_base_pages").AsGodotObject() as Texture2D)?.GetImage()
                ??throw new InvalidOperationException("The mission color page map was not published.");
            int count=0; for(int y=0;y<pages.GetHeight();y++)for(int x=0;x<pages.GetWidth();x++)if(pages.GetPixel(x,y).A>0)count++;
            if(count!=170) throw new InvalidOperationException($"Expected all 170 original color pages, found {count}.");
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            using var picture=GetViewport().GetTexture().GetImage();
            if(picture.SavePng(Path.Combine(directory,"authored-mission-local-tiles.png"))!=Error.Ok)throw new IOException("Could not save mission tile evidence.");
            GD.Print("Mission local tiles GPU: 170 original color plus 170 DDS normals, shared pages, two independent lights/cameras and reload.");

            ObserverView View(JulianInstant at)
            {
                var snapshot=session.World.Graph.Evaluate(at);
                if(!snapshot.TryState(id,out var body))throw new InvalidOperationException("The mission globe has no state.");
                var toward=snapshot.SolarSystem!.Value.Sun.PositionKilometers-body.PositionKilometers; toward/=toward.Length;
                var up=new Vector3d(0,0,1)-toward*toward.Z;
                return new(at,CameraMode.Solar,id,false){Navigation=new(body.PositionKilometers,10000,0,0,id,true)
                    {Placement=new(toward*10000,toward*-1,up/up.Length)}};
            }
        }
        finally {System.Environment.SetEnvironmentVariable(variable,previous);await scene.ReloadAddOns();scene.LoadScenario(saved);}

        object CopyMap(SurfaceTilePyramid pyramid,string kind)
        {
            var files=new Dictionary<string,string>();
            foreach(var (address,path) in pyramid.Tiles)
            {
                string relative=$"{kind}/{address.Level}-{address.Column}-{address.Row}.dds";
                string output=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.Copy(path,output,true);
                files.Add($"{address.Level}/{address.Column}/{address.Row}",relative);
            }
            return new {type="NameTemplate",template=kind+"/%level/%column/%row.dds",levelCount=pyramid.LevelCount,
                tileSize=pyramid.TileSize,tileBorderThickness=pyramid.Border,localFiles=files};
        }
    }
}
