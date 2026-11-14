// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseRedshiftScript(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_REDSHIFT_PROBE") is not ("1" or "only"))
            return;
        string source = Path.Combine(System.Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES")!, "z-dist.celx");
        var session = scene.Session!;
        var left = session[0] with { Playing = false };
        var right = left with { Epoch = left.Epoch.AddSeconds(7200) };
        string original = Path.Combine(directory, "before-redshift.stscene");
        string prepared = Path.Combine(directory, "redshift-observers.stscene");
        if (!scene.SaveScenario(original))
            throw new IOException("Could not save the observers before the redshift script.");
        ScenarioFile.Save(prepared, new([left, right], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(prepared))
            throw new InvalidOperationException("Could not set up independent redshift observers.");
        var renderer = scene.Renderer(0);
        var running = scene.RunScript(source) ?? throw new InvalidOperationException("Could not run the original redshift script.");
        try
        {
            // Keep loading deterministic without coupling Lua's bounded slices to GPU frame throughput.
            for (int slice = 0; slice < 6000 && session.Notice(0) is null && !running.Finished; slice++)
                running.Step(.05);
            if (running.Error is not null || session.Markers(0).Count < 10_000 ||
                session.Notice(0) is not { } initial || !initial.Text.StartsWith("Milky Way:", StringComparison.Ordinal))
                throw new InvalidOperationException("The original script did not initialize the galaxy marks: " + running.Error);
            session.UpdateCamera(0, session.World.Graph.Evaluate(left.Epoch), 5);
            var andromeda = new ObjectId("dso/m31");
            session.SelectObject(0, andromeda);
            for (int slice = 0; slice < 10 && session.Markers(0).GetValueOrDefault(andromeda).Size < 10; slice++)
                running.Step(.05);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

            var selected = renderer.VisibleScriptMarkers.Single(item => item.Marker is { Size: 10, Color: { X: 0, Y: 1, Z: 0 } });
            if (scene.Renderer(1).VisibleScriptMarkers.Count != 0 || scene.Renderer(1).VisibleScriptNotice is not null ||
                session[1] != right || renderer.VisibleScriptNotice is not { } notice || !notice.Contains("redshift z =", StringComparison.Ordinal))
                throw new InvalidOperationException("The marks or reports escaped their observer.");
            using var view = renderer.GetViewport().GetTexture().GetImage();
            var center = new Vector2I((int)selected.Center.X, (int)selected.Center.Y);
            Color pixel = view.GetPixel(center.X, center.Y);
            if (pixel.G < .7 || pixel.R > .3 || pixel.B > .3)
                throw new InvalidOperationException($"The green selection disk is absent at its projected center: {pixel}.");
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(Path.Combine(directory, "redshift-catalogue.png")) != Error.Ok)
                throw new IOException("Could not capture the interactive redshift view.");
            GD.Print($"Original redshift CELX: {session.Markers(0).Count} galaxy marks, {renderer.VisibleScriptMarkers.Count} projected, a green selected disk, an independent second view and a 1000 Mpc flight.");
            running.Stop();
            string classic = Path.Combine(directory, "catalogue-marker.cel");
            File.WriteAllText(classic, """
                {
                    unmarkall {}
                    renderflags { set "markers" }
                    mark { object "Andromeda Galaxy" symbol "filledsquare" color [1 0 1] size 24 }
                }
                """);
            string converted = Path.ChangeExtension(classic, ".lua");
            File.WriteAllText(converted, TourConverter.ConvertFile(classic).Lua);
            var markerScript = scene.RunScript(converted) ?? throw new InvalidOperationException("Could not run the classic marker.");
            for (int frame = 0; frame < 10 && !markerScript.Finished; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            if (markerScript.Error is not null || !session.Markers(0).ContainsKey(andromeda))
                throw new InvalidOperationException("The classic script could not mark the original galaxy: " + markerScript.Error);
            var square = renderer.VisibleScriptMarkers.Single(item => item.Marker.Shape == ScriptMarkerShape.FilledSquare);
            using var markedView = renderer.GetViewport().GetTexture().GetImage();
            // This point is inside the square, beyond the radius of a disk of the same size.
            Color corner = markedView.GetPixel((int)square.Center.X + 10, (int)square.Center.Y + 10);
            if (corner.R < .8 || corner.G > .2 || corner.B < .8 ||
                scene.Renderer(1).VisibleScriptMarkers.Count != 0)
                throw new InvalidOperationException("The classic marker did not render as an isolated magenta square.");
            GD.Print("Classic marker GPU: converted a filled square on original M31, sampled its corner and preserved the second view.");
        }
        finally
        {
            running.Stop();
            if (!scene.LoadScenario(original))
                throw new InvalidOperationException("Could not restore the observers after the redshift script.");
        }
    }
}
