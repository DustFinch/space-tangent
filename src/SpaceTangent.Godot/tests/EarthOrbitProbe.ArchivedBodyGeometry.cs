// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedBodyGeometry(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ARCHIVED_BODY_GPU") != "1")
            return;
        string archive = System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_RECORDING")!;
        string saved = Path.Combine(directory, "before-archived-geometry.stscene");
        if (!scene.SaveScenario(saved) || !scene.StartReplay(archive))
            throw new InvalidOperationException("Could not open the original geometry recording.");
        var renderer = scene.Renderer(0);
        var camera = renderer.NavigationCameraNode;
        if (renderer.ArchivedGeometry is not { } geometry ||
            renderer.ArchivedBody("Sol/Earth/Moon") is not { Visible: true } moon)
            throw new InvalidOperationException("The original recorded lunar body is unavailable.");

        var record = geometry.Bodies.Single(body => body.Name == "Sol/Earth/Moon");
        Vector3 local = camera.Basis.Inverse() * (moon.Position - camera.Position);
        double scale = moon.Basis.X.Length() / record.Radius;
        if ((local / (float)scale).DistanceTo(new((float)record.CameraPosition.X, (float)record.CameraPosition.Y,
            (float)record.CameraPosition.Z)) > .0002f)
            throw new InvalidOperationException("The rendered lunar geometry diverged from the recorded transform.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = renderer.GetViewport().GetTexture().GetImage();
        Vector2 center = camera.UnprojectPosition(moon.Position);
        Color disk = image.GetPixel((int)center.X, (int)center.Y);
        if (disk.R + disk.G + disk.B < .2f)
            throw new InvalidOperationException("The recorded lunar disc is absent at its projected center.");
        if (image.SavePng(Path.Combine(directory, "archived-lunar-geometry.png")) != Error.Ok)
            throw new IOException("Could not capture the recorded lunar geometry.");
        if (!scene.LoadScenario(saved) || renderer.ArchivedBody("Sol/Earth/Moon") is not null)
            throw new InvalidOperationException("Leaving replay did not restore live geometry.");
        GD.Print("Archived geometry GPU: rendered the recorded lunar transform and visible disc, then restored the live view.");
        string? orbits = System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_ORBITS");
        if (orbits is null)
            return;
        if (!scene.StartReplay(orbits) || renderer.ArchivedGeometry is not { Orbits.Count: 66, Labels.Count: 115 } ||
            renderer.ArchivedOrbitSurfaces != 66 || !renderer.ArchivedLabels.Any(label => label.Visible && label.Text == "Альтаир"))
            throw new InvalidOperationException("The original Windows recording did not publish its saved orbits and Unicode labels.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var overlays = renderer.GetViewport().GetTexture().GetImage();
        if (overlays.SavePng(Path.Combine(directory, "archived-orbits-labels.png")) != Error.Ok)
            throw new IOException("Could not capture the saved orbit and label overlay.");
        if (!scene.LoadScenario(saved) || renderer.ArchivedOrbitSurfaces != 0 || renderer.ArchivedLabels.Count != 0)
            throw new InvalidOperationException("The saved orbit and label overlays persisted outside replay.");
        GD.Print("Archived overlays GPU: original Windows capture published 66 orbit strips and 115 labels including Cyrillic, then cleared them on exit.");
    }
}
