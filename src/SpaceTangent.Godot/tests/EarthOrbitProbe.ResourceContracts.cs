// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseResourceContracts(SolarSystemScene scene,string directory)
    {
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
        await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
        GD.Print("Resource contracts GPU: stable legacy pack keys, malformed/oversized manifests, PNG/JPEG/WebP artwork cache and silent WAV lifecycle.");
    }
}
