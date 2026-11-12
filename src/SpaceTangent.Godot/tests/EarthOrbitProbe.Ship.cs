// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task CaptureShip(SolarSystemScene scene, string directory)
    {
        if (DisplayServer.GetName() == "headless")
            return;
        if (!scene.LoadScenario(Path.Combine(directory, "earth-day.stscene")) || !SimulationViewport.ShipModelAvailable)
            throw new InvalidOperationException("The exterior ship check needs the Earth scene and imported shuttle.");
        var session = scene.Session!;
        session.BoardShip(0);
        session.AdjustCamera(0, .82, -.2, 1);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!scene.Renderer(0).ShipHullVisible)
            throw new InvalidOperationException("The ship hull was not drawn.");
        using var exterior = GetViewport().GetTexture().GetImage();
        session.AdjustCamera(0, -.82, .2, 1);
        session.ToggleShipCockpit(0);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var interior = GetViewport().GetTexture().GetImage();
        if (scene.Renderer(0).ShipHullVisible)
            throw new InvalidOperationException("The ship hull must be hidden in cockpit mode.");
        if (exterior.SavePng(Path.Combine(directory, "ship-exterior.png")) != Error.Ok ||
            interior.SavePng(Path.Combine(directory, "ship-cockpit.png")) != Error.Ok)
            throw new IOException("Could not save the two ship camera captures.");
        GD.Print("Ship cameras rendered on the GPU.");
    }
}
