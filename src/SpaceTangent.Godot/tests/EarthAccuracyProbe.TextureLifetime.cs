// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthAccuracyProbe
{
    private static async Task ExerciseTextureLifetime()
    {
        const string variable = "SPACETANGENT_PLANET_TEXTURES";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        var root = Directory.CreateTempSubdirectory("st-texture-lifetime-");
        try
        {
            System.Environment.SetEnvironmentVariable(variable, root.FullName);
            string first = Path.Combine(root.FullName, PlanetCatalogue.Planets[0].Texture);
            using (var source = Image.CreateEmpty(32, 16, false, Image.Format.Rgb8))
            {
                source.Fill(Colors.Brown);
                if (source.SaveJpg(first) != Error.Ok)
                    throw new InvalidOperationException("Could not create the texture lifecycle fixture.");
            }
            bool checkedOwner = false;
            try
            {
                await PlanetTextures.LoadAsync(canPublish: () => { checkedOwner = true; return false; });
                throw new InvalidOperationException("Textures published after their owner was closed.");
            }
            catch (OperationCanceledException) when (checkedOwner) { }

            int ownerChecks = 0;
            try
            {
                await PlanetTextures.LoadAsync(canPublish: () => ++ownerChecks < 2);
                throw new InvalidOperationException("A late closed owner retained a partially published batch.");
            }
            catch (OperationCanceledException) when (ownerChecks == 2) { }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await PlanetTextures.LoadAsync(cancellation.Token);
                throw new InvalidOperationException("Cancelled texture loading succeeded.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

            // A late decode error must unwind a batch containing an already decoded image. Godot's loader logs the
            // deliberately invalid JPEG; the managed failure is expected and the next valid batch must still publish.
            string corrupt = Path.Combine(root.FullName, "venus-clouds.jpg");
            File.WriteAllText(corrupt, "deliberately invalid texture");
            try
            {
                await PlanetTextures.LoadAsync();
                throw new InvalidOperationException("Malformed planetary map was accepted.");
            }
            catch (InvalidDataException) { }
            File.Delete(corrupt);
            PlanetTextures maps = await PlanetTextures.LoadAsync();
            try
            {
                if (maps.Surfaces.Count != 1)
                    throw new InvalidOperationException("The valid texture batch did not recover after cancellation/failure.");
                // Compare the worker-prepared layer to the former GPU-readback/resize path, independently.
                using Image expected = maps.Surfaces[PlanetCatalogue.Planets[0].Id].GetImage();
                expected.ClearMipmaps();
                expected.Convert(Image.Format.Rgb8);
                expected.Resize(512, 256, Image.Interpolation.Lanczos);
                expected.GenerateMipmaps();
                using Image actual = maps.GroundSurfaceMaps!.GetLayerData(0);
                if (!expected.GetData().SequenceEqual(actual.GetData()))
                    throw new InvalidOperationException("Worker-prepared ground map differs from the former resize path.");
            }
            finally
            {
                foreach (Texture2D texture in maps.Surfaces.Values.Concat(maps.Rings.Values).Concat(maps.StellarSurfaces.Values))
                    texture.Dispose();
                maps.VenusClouds?.Dispose();
                maps.GroundSurfaceMaps?.Dispose();
            }
            GD.Print("Texture lifetime passed: closed owner, cancellation, late malformed map, successful recovery.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
            root.Delete(true);
        }
    }
}
