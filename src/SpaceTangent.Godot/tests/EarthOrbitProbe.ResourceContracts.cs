// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseResourceContracts(SolarSystemScene scene,string directory)
    {
        ExerciseAddonSettingsContracts(directory);
        string root=Path.Combine(directory,"named-pack"); Directory.CreateDirectory(root);
        var location=new LocalDataPath(root,"","","",true);
        var bare=LegacyPackKeys.For(location);
        if(bare.Length!=2||bare[0]!="named-pack"||!bare[1].StartsWith("addon-root:",StringComparison.Ordinal)||bare[1].Length!=75)
            throw new InvalidOperationException("An unmanifested root requires its directory name and stable path identity.");
        File.WriteAllText(Path.Combine(root,"pack.json"),"{\"id\":\"authored-pack\"}");
        var identified=LegacyPackKeys.For(location);
        if(!identified.SequenceEqual(new[]{"authored-pack",bare[0],bare[1]}))
            throw new InvalidOperationException("A manifest ID must supplement rather than replace stable legacy keys.");
        foreach(string malformed in new[]{"{broken","[]","{\"id\":7}"})
        {
            File.WriteAllText(Path.Combine(root,"pack.json"),malformed);
            if(!LegacyPackKeys.For(location).SequenceEqual(bare)) throw new InvalidOperationException("Invalid manifests changed the legacy root identity.");
        }
        using(var large=File.Create(Path.Combine(root,"pack.json"))) large.SetLength(64L*1024*1024+1);
        if(!LegacyPackKeys.For(location).SequenceEqual(bare)) throw new InvalidOperationException("Oversized manifests must retain only root keys.");
        File.Delete(Path.Combine(root,"pack.json"));

        var sky=BrightSkyData.Load();
        try
        {
            using var image=Image.CreateEmpty(4,4,false,Image.Format.Rgba8); image.Fill(new(.1f,.8f,.2f));
            byte[][] images=[image.SavePngToBuffer(),image.SaveJpgToBuffer(),image.SaveWebpToBuffer()];
            string[] extensions=[".png",".jpg",".webp"];
            for(int i=0;i<images.Length;i++)
            {
                string path=Path.Combine(directory,"art"+extensions[i]); File.WriteAllBytes(path,images[i]);
                var art=new SkyCultureArtwork(path,4,4,default,default,default);
                var texture=sky.ArtTexture(art)??throw new InvalidOperationException("An authored artwork did not decode.");
                using var decoded=texture.GetImage(); Color pixel=decoded.GetPixel(0,0);
                if(pixel.G<.6f||pixel.R>.3f||!ReferenceEquals(texture,sky.ArtTexture(art)))
                    throw new InvalidOperationException("Artwork colours or shared cache identity changed.");
            }
            var absent=new SkyCultureArtwork(Path.Combine(directory,"missing.png"),4,4,default,default,default);
            if(sky.ArtTexture(absent)is not null||sky.ArtTexture(absent)is not null) throw new InvalidOperationException("Missing artwork must be cached as unavailable.");
            string tiny=Path.Combine(directory,"tiny.png"); File.WriteAllBytes(tiny,[1,2,3]);
            if(sky.ArtTexture(absent with {ImagePath=tiny})is not null) throw new InvalidOperationException("A truncated artwork cannot publish a texture.");
            for(int i=0;i<130;i++)
                if(sky.ArtTexture(absent with {ImagePath=Path.Combine(directory,$"absent-art-{i}.png")})is not null)
                    throw new InvalidOperationException("Cache rollover must not turn absent artwork into textures.");
            using(var wide=Image.CreateEmpty(4097,1,false,Image.Format.Rgba8))
            {
                string path=Path.Combine(directory,"oversized-art.png");
                if(wide.SavePng(path)!=Error.Ok) throw new IOException("Could not save the artwork dimension witness.");
                if(sky.ArtTexture(absent with {ImagePath=path})is not null)
                    throw new InvalidOperationException("Artwork dimensions must be bounded before publishing a texture.");
            }
            using(var large=Image.CreateEmpty(4096,4096,false,Image.Format.Rgb8))
            {
                large.Fill(new(.1f,.8f,.2f));
                string first=Path.Combine(directory,"large-art-a.png"),second=Path.Combine(directory,"large-art-b.png");
                if(large.SavePng(first)!=Error.Ok) throw new IOException("Could not save the artwork cache witness.");
                File.Copy(first,second,true);
                var firstArt=absent with {ImagePath=first};
                var original=sky.ArtTexture(firstArt)??throw new InvalidOperationException("The first bounded artwork must decode.");
                if(sky.ArtTexture(absent with {ImagePath=second})is null||ReferenceEquals(original,sky.ArtTexture(firstArt)))
                    throw new InvalidOperationException("The pixel budget must evict the previous artwork and allow it to be decoded again.");
            }
        }
        finally {sky.Surveys.Dispose();}

        string wave=Path.Combine(directory,"silence.wav");
        using(var writer=new BinaryWriter(File.Create(wave)))
        {
            writer.Write("RIFF"u8);writer.Write(36+16000);writer.Write("WAVEfmt "u8);writer.Write(16);
            writer.Write((short)1);writer.Write((short)1);writer.Write(8000);writer.Write(16000);writer.Write((short)2);writer.Write((short)16);
            writer.Write("data"u8);writer.Write(16000);writer.Write(new byte[16000]);
        }
        var media=new ScriptMediaPlayer(scene.Renderer(0));
        try
        {
            media.Present(directory,new ScriptMediaRequest.Sound("silence.wav",0,1,false));
            var audio=scene.Renderer(0).GetChildren().OfType<AudioStreamPlayer>().Single(player=>player.Name=="ScriptSound1");
            if(!media.Active||audio.Stream is not AudioStreamWav||audio.VolumeDb> -90)
                throw new InvalidOperationException("A silent local WAV must retain its isolated channel and volume.");
            audio.EmitSignal(AudioStreamPlayer.SignalName.Finished);
            if(media.Active) throw new InvalidOperationException("A completed one-shot sound must release its channel.");
            media.Present(directory,new ScriptMediaRequest.Sound("silence.wav",0,2,true));
            audio=scene.Renderer(0).GetChildren().OfType<AudioStreamPlayer>().Single(player=>player.Name=="ScriptSound2");
            audio.EmitSignal(AudioStreamPlayer.SignalName.Finished);
            if(!media.Active) throw new InvalidOperationException("A looping sound must retain its channel after completion.");
            media.Present(directory,new ScriptMediaRequest.Sound("",0,2,false));
            if(media.Active) throw new InvalidOperationException("An explicit stop must clear the looping channel.");
        }
        finally {media.Clear();}
        var bounded=new ScriptMediaPlayer(scene.Renderer(0));
        try
        {
            for(int i=0;i<4;i++) bounded.Present(directory,new ScriptMediaRequest.Overlay("art.png",60,.5,.5,4,4,1));
            Reject(()=>bounded.Present(directory,new ScriptMediaRequest.Overlay("art.png",60,.5,.5,4,4,1)),typeof(InvalidDataException));
            bounded.Clear();
            File.WriteAllBytes(Path.Combine(directory,"unsupported.bin"),[1]);
            Reject(()=>bounded.Present(directory,new ScriptMediaRequest.Sound("unsupported.bin",0,1,false)),typeof(InvalidDataException));
            Reject(()=>bounded.Present(directory,new ScriptMediaRequest.Overlay("unsupported.bin",1,.5,.5,0,0,1)),typeof(InvalidDataException));
            File.WriteAllBytes(Path.Combine(directory,"empty.png"),[]);
            Reject(()=>bounded.Present(directory,new ScriptMediaRequest.Overlay("empty.png",1,.5,.5,0,0,1)),typeof(InvalidDataException));
            using(var wide=Image.CreateEmpty(4097,1,false,Image.Format.Rgba8))
                if(wide.SavePng(Path.Combine(directory,"wide.png"))!=Error.Ok) throw new IOException("Could not write the image dimension witness.");
            Reject(()=>bounded.Present(directory,new ScriptMediaRequest.Overlay("wide.png",1,.5,.5,0,0,1)),typeof(InvalidDataException));
            bool refused=false;
            for(int i=0;i<100&&!refused;i++)
                try {bounded.Present(directory,new ScriptMediaRequest.Sound("",0,9,false));}
                catch(InvalidOperationException) {refused=true;}
            if(!refused||bounded.Active) throw new InvalidOperationException("The media request budget must bound repeated empty stops without retaining resources.");
        }
        finally {bounded.Clear();}

        const string magnitudeVariable="SPACETANGENT_SATELLITE_MAGNITUDES";
        string? prior=System.Environment.GetEnvironmentVariable(magnitudeVariable);
        string magnitudes=Path.Combine(directory,"magnitudes.txt");
        try
        {
            File.WriteAllText(magnitudes,"26997 4.5\n-1 3\n42 NaN\nbad\n26997 5\n12345 6.5\n");
            System.Environment.SetEnvironmentVariable(magnitudeVariable,magnitudes);
            var data=BrightSkyData.Load();
            try
            {
                if(data.SatelliteMagnitudes.Count!=2||data.SatelliteMagnitudes[new ObjectId("norad/26997")]!=5)
                    throw new InvalidOperationException("Magnitude imports must reject invalid rows and retain the last valid update.");
            }
            finally {data.Surveys.Dispose();}
            using(var oversized=File.Create(magnitudes)) oversized.SetLength(1024*1024+1);
            data=BrightSkyData.Load();
            try {if(data.SatelliteMagnitudes.Count!=0) throw new InvalidOperationException("An oversized magnitude table must not publish partial rows.");}
            finally {data.Surveys.Dispose();}
        }
        finally {System.Environment.SetEnvironmentVariable(magnitudeVariable,prior);}
        foreach(var input in new[]{("SPACETANGENT_MINOR_BODY_CSV",33L*1024*1024),("SPACETANGENT_CONSTELLATION_BOUNDARIES",1024L*1024+1)})
        {
            string? saved=System.Environment.GetEnvironmentVariable(input.Item1);
            string path=Path.Combine(directory,input.Item1+".txt");
            try
            {
                using(var file=File.Create(path)) file.SetLength(input.Item2);
                System.Environment.SetEnvironmentVariable(input.Item1,path);
                var data=BrightSkyData.Load();
                try
                {
                    if(input.Item1.Contains("MINOR_BODY",StringComparison.Ordinal)?data.MinorBodies.Length!=0:data.Boundaries.Count!=0)
                        throw new InvalidOperationException("An oversized sky catalogue must not publish partial contents.");
                }
                finally {data.Surveys.Dispose();}
            }
            finally {System.Environment.SetEnvironmentVariable(input.Item1,saved);}
        }
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        GD.Print("Resource contracts GPU: stable legacy pack keys, malformed/oversized manifests, PNG/JPEG/WebP artwork cache and silent WAV lifecycle.");
        static void Reject(Action action,Type expected)
        {
            try {action();}
            catch(Exception error) when(expected.IsInstanceOfType(error)) {return;}
            throw new InvalidOperationException("The resource request was accepted beyond its documented bound.");
        }
    }
}
