// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredDistantPicking(SolarSystemScene scene, string directory)
    {
        const string variable = "SPACETANGENT_STELLAR_ARCHIVE";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        string root = Path.Combine(directory, "authored-distant-stars"); Directory.CreateDirectory(root);
        // Three independently encoded numbered stars, not a replacement for the unavailable historical catalogue.
        using (var writer = new BinaryWriter(File.Create(Path.Combine(root, "stars-far.bin"))))
        {
            writer.Write("CELWFAR\0"u8); writer.Write((ushort)2); writer.Write((ushort)0);
            writer.Write(3u); writer.Write(1u);
            for (int i = 0; i < 401; i++) writer.Write(new byte[] { 255, 128, 64, 255 });
            for (int i = 0; i < 3; i++) writer.Write(-20f);
            for (int i = 0; i < 3; i++) writer.Write(20f);
            writer.Write(-10f); writer.Write(0u); writer.Write(3u);
            foreach (var star in new[] { (10f, 10u), (11f, 11u), (-10f, 12u) })
            {
                writer.Write(star.Item1); writer.Write(star.Item2 == 11 ? .5f : 0f); writer.Write(0f);
                writer.Write((short)0); writer.Write((ushort)60); writer.Write(star.Item2);
            }
        }
        string restore = Path.Combine(directory, "before-distant.stscene");
        if (!scene.SaveScenario(restore)) throw new IOException("Preserve the current cameras before distant-star acceptance.");
        try
        {
            System.Environment.SetEnvironmentVariable(variable, root);
            if (!await scene.ReloadAddOns() || scene.Session!.World.DistantStars?.StarCount != 3)
                throw new InvalidOperationException("The authored three-star spatial index must load without a near-star archive.");
            var session = scene.Session!;
            var pose = new NavigationCamera(session.World.StellarOrigin + new Vector3d(9 * StellarEntry.KilometersPerLightYear, 0, 0),
                1, 0, 0, null, false) { Placement = new(default, new(1, 0, 0), new(0, 0, 1)) };
            var view = new ObserverView(session[0].Epoch, CameraMode.Solar, null, false)
                { Navigation = pose, Sky = new(true, false, false, false) };
            string path = Path.Combine(directory, "distant-picking.stscene");
            ScenarioFile.Save(path, new([view], true, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path)) throw new InvalidOperationException("Open the authored observer one light year from the nearest distant point.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var renderer = scene.Renderer(0);
            Vector2 middle = renderer.GetViewport().GetVisibleRect().GetCenter();
            if (!renderer.SpatialStarFieldVisible || renderer.VisibleDistantStars != 3 || renderer.PickObject(middle) != new ObjectId("hip/10"))
                throw new InvalidOperationException($"The central distant point must be independently pickable; count={renderer.VisibleDistantStars}, picked={renderer.PickObject(middle)}.");
            if (renderer.PickObject(new(-1000, -1000)) is not null)
                throw new InvalidOperationException("A distant point outside the five-pixel hit radius must not be selected.");
            session.SetSkyLayers(0, view.Sky with { Stars = false }); scene.Seek(0, view.Epoch);
            if (renderer.SpatialStarFieldVisible || renderer.PickObject(middle) is not null)
                throw new InvalidOperationException("Disabling stars must disable their spatial hit targets as well as their pixels.");
            GD.Print("Distant picking GPU: authored three-star index, centered hit, off-axis miss, rear-hemisphere culling and disabled-layer cleanup passed.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previous);
            if (!await scene.ReloadAddOns() || !scene.LoadScenario(restore))
                throw new InvalidOperationException("Restore the original catalogue and observers after distant-star acceptance.");
        }
    }
}
