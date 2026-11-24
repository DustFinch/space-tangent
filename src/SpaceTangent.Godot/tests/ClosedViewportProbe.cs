// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class ClosedViewportProbe : Node
{
    public override async void _Ready()
    {
        SolarSystemScene? scene = null;
        int exit = 1;
        try
        {
            GetTree().Root.Size = new(1600, 900);
            scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            await ProbeHost.Until(this, () => scene.InitializationFinished, 60, "Closed-view startup timed out.");
            var session = scene.Session ?? throw new InvalidOperationException("Real simulation required.");
            session.SetPlaying(0, false);
            scene.SetMode(0, CameraMode.Satellites);
            var renderer = scene.Renderer(0);
            var frame = session.World.Graph.Evaluate(session[0].Epoch, session.DeltaT);
            ulong projection = renderer.MissionProjectedFrusta.Mesh.GetInstanceId();
            Node parent = renderer.GetParent();
            parent.RemoveChild(renderer); parent.AddChild(renderer);
            Require(GodotObject.IsInstanceIdValid(projection) && renderer.Draw(session[0], frame, session.DeltaT),
                "Temporary layout detachment must preserve resources and drawing.");
            int nodes = renderer.GetChildCount();
            renderer.CloseOwnedResources(); renderer.CloseOwnedResources();
            Require(!GodotObject.IsInstanceIdValid(projection), "Final close must detach and destroy the owned projection mesh.");
            CheckClosed(renderer);
            Require(renderer.GetChildCount() == nodes, "Closed entry points must not create replacement nodes.");
            for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(renderer.GetChildCount() == nodes, "Frame callbacks must not resurrect a closed view.");
            var early = new SimulationViewport();
            early.CloseOwnedResources(); AddChild(early);
            Require(early.GetChildCount() == 0, "Ready must not initialize an already closed renderer.");
            CheckClosed(early); RemoveChild(early); early.Free();
            CheckClosed(early);
            GD.Print("SpaceTangent closed viewport probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally { ProbeHost.FreeScene(this, scene); ProbeHost.Complete(this, exit); }
    }

    private static void CheckClosed(SimulationViewport view)
    {
        Require(!view.Draw(default, null!, default), "Closed drawing must be a no-op even with unavailable frame data.");
        view.DrawReferenceMarks(default, null);
        view.DrawShip(null, null);
        view.DrawScriptMarkers(default, null!, null!, default);
        view.ReplaceSatellites([]); view.ReplaceMission(null, null, null);
        view.ReplaceLunarTerrain(null); view.ReplaceMarsTerrain(null); view.ReplaceImportedBodies([]);
        view.SetCatalogueWorld(null!); view.SetSkyData(null!); view.SetDeepSkySprites(null);
        view.SetMissionAnnotations([], default); view.SetScriptNotice(null);
        view.SetPlanetTextures(null!); view.SetWorldAtmospheres(null!, null!);
        view.SetPlanetMoonModels(null!); view.SetEarthOrbitTextures(null!, null);
        view.SetSurfaceTextures(null!); view.SetSurfaceControls(true, true); view.SetSurfacePitch(0);
        view.SetLandscape(null, false); view.SetExoplanetTextures(null!); view.SetMinorBodySwarm([]);
        view.SetDemoTerrainProbeLight(default, null); view.SetDemoTerrainShadows(default, false);
        view.HideArchivedComets(); view._Process(0); view._Ready();
        Require(view.PlanetOrbitCount == 0 && view.MissionFootprintCount == 0 && view.MissionTrailPointCount == 0 &&
            view.MissionTrailSamples.Count == 0 && view.VisibleDistantStars == 0 && view.ArchivedStarCount == 0 &&
            !view.MissionProjectionVisible && !view.MissionFootprintVisible && !view.ReferenceMarksVisible &&
            !view.SpatialStarFieldVisible && !view.EyepieceVisible && !view.HasPendingPaths &&
            view.PointerDirection(default) is null && !view.PublishDemoTerrain(), "Closed queries must return neutral values.");
        view.CloseOwnedResources();
    }
    private static void Require(bool value, string message) => ProbeHost.Require(value, message);
}
