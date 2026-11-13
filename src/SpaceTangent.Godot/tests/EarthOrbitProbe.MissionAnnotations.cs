// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionAnnotations(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ANNOTATION_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_ANNOTATION_CORPUS") is not { Length: > 0 } root)
            return;

        const string input = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? original = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-mission-annotations.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observers before annotation verification.");
        var session = scene.Session!;
        var deltaT = session.DeltaT;
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var first = Observer(epoch.AddSeconds(300));
        var second = Observer(epoch.AddSeconds(900));
        const string title = "RADAR observation progress:";
        try
        {
            foreach (string format in new[] { "source", "packed" })
            {
                System.Environment.SetEnvironmentVariable(input, Path.Combine(root, "p4-annotation-" + format));
                if (!await scene.ReloadAddOns())
                    throw new InvalidOperationException($"Could not load the {format} annotation catalogue.");
                string path = Path.Combine(directory, $"mission-annotations-{format}.stscene");
                ScenarioFile.Save(path, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() ||
                    session[0] != first || session[1] != second || session.DeltaT != deltaT)
                    throw new InvalidOperationException("Both annotation cameras and Delta T must survive reload.");
                if (!scene.Renderer(0).VisibleMissionAnnotations.SequenceEqual(new[] { title, "  preparing" }) ||
                    !scene.Renderer(1).VisibleMissionAnnotations.SequenceEqual(new[] { title }))
                    throw new InvalidOperationException("Annotations must follow each observer's epoch.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                int redLeft = RedGlyphs(scene.Renderer(0)), redRight = RedGlyphs(scene.Renderer(1));
                if (redLeft < 12 || redRight > 2)
                    throw new InvalidOperationException($"Unexpected annotation pixels in the two views: {redLeft}/{redRight}.");
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                    throw new IOException("Could not capture mission annotations.");
                GD.Print($"Mission annotations GPU ({format}): 2/1 lines at different epochs, red glyphs {redLeft}/{redRight}, both observers and Delta T restored.");
            }
            string expired = Path.Combine(directory, "mission-annotations-expired.stscene");
            var late = Observer(epoch.AddSeconds(3601));
            ScenarioFile.Save(expired, new([late, second], false, 0, 0, deltaT));
            if (!scene.LoadScenario(expired) || scene.Renderer(0).VisibleMissionAnnotations.Count != 0 ||
                scene.Renderer(1).VisibleMissionAnnotations.Count != 1)
                throw new InvalidOperationException("An expired line must disappear only from its own observer.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, original);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior) ||
            scene.Renderer(0).VisibleMissionAnnotations.Count != 0)
            throw new InvalidOperationException("Annotations persisted after removing their source.");

        ObserverView Observer(JulianInstant at)
        {
            var target = SolarSystemSample.EarthId;
            var earth = session.World.Ephemeris.Sample(at).Earth;
            return new(at, CameraMode.Solar, target, false)
                { Navigation = new NavigationCamera(earth.PositionKilometers, 60_000, .4, .35, target, true) };
        }

        static int RedGlyphs(SimulationViewport view)
        {
            using var image = view.GetViewport().GetTexture().GetImage();
            int red = 0;
            int fromX = (int)(.05 * image.GetWidth()), fromY = (int)(.30 * image.GetHeight()) + 20;
            for (int y = fromY; y < fromY + 21; y++)
            for (int x = fromX; x < fromX + 95; x++)
            {
                Color pixel = image.GetPixel(x, y);
                if (pixel.R > .17f && pixel.R > 3 * Math.Max(pixel.G, pixel.B))
                    red++;
            }
            return red;
        }
    }
}
