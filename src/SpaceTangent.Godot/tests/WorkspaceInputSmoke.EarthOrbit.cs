// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseEarthOrbit()
    {
        var canvas = Find<SubViewportContainer>("ViewCanvas0");
        await Click(canvas);
        var search = Find<Button>("SearchTab");
        if (!search.ButtonPressed)
            await Click(search);
        await FillField("ObjectQuery", "Earth");
        await Click(Result(SolarSystemSample.EarthId));
        await Click(Find<Button>("GoToObject"));
        Require(Session.IsFlying(0), "The Earth search result must be a flight target.");
        await Until(() => !Session.IsFlying(0), "Flying to textured Earth");
        Require(Session[0].Navigation?.Target == SolarSystemSample.EarthId && _scene.Renderer(0).EarthOrbitAtmosphereActive,
            "Flight must end at the orbital Earth renderer with the atmospheric shell active.");

        Window root = Tree.Root;
        Vector2 point = root.GetScreenTransform().AffineInverse() * (canvas.GetScreenTransform() * (canvas.Size / 2));
        for (int step = 0; step < 8; step++)
        {
            using var wheel = new InputEventMouseButton
            {
                Position = point, GlobalPosition = point, ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1
            };
            root.PushInput(wheel, inLocalCoords: true);
            await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
        Require(Session[0].Navigation is { RangeKilometers: > 6378 and < 6478 } && _scene.Renderer(0).EarthOrbitAtmosphereActive,
            "Orbit zoom must approach below the top of the atmospheric shell while remaining above the ground.");

        _scene.SetMode(0, CameraMode.Surface);
        Require(Session[0].Mode == CameraMode.Surface && Session[0].Navigation is null,
            "The surface observer mode must switch from orbital Earth to the surface observer.");
        GD.Print("UI: flew to textured Earth, zoomed below the top of the atmosphere, and switched to the surface observer");
    }
}
