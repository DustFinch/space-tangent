// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Addons;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class AddonLifecycleProbe : Node
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
            await Until(() => scene.InitializationFinished, "initialization");
            var session = scene.Session ?? throw new InvalidOperationException("A real simulation is required.");
            Require(session.World.Satellites.Any(item => item.Id == new ObjectId("norad/26997")), "Jason-1 must be loaded.");
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            session.SetPlaying(0, false);
            scene.SetMode(0, CameraMode.Satellites);
            if (session.ViewCount == 1) scene.ToggleSplit();
            scene.ShowFullMoon(1);
            session.Synchronize(false, 0);
            session.Step(1, 3600);
            int[] identities = Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity).ToArray();
            string settings = System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH")
                ?? throw new InvalidOperationException("Use isolated add-on settings for this probe.");
            string statePath = Path.ChangeExtension(settings, ".lifecycle.stscene");
            ScenarioFile.Save(statePath, session.CaptureScenario(0));
            byte[] before = File.ReadAllBytes(statePath);
            string? previousSettings = File.Exists(settings) ? File.ReadAllText(settings) : null;

            Task<bool> older = scene.ReloadAddOns(LocalAddonSettings.Empty.SetDefault(AddonSourceKind.Science, false));
            Require(!older.IsCompleted, "The real preparation must yield before issuing the overlapping request.");
            Task<bool> latest = scene.ReloadAddOns();
            await Until(() => older.IsCompleted && latest.IsCompleted, "overlapping reloads");
            Require(!await older && await latest, "Only the latest request may succeed.");
            Require(ReferenceEquals(session, scene.Session), "Reload must preserve the live session identity.");
            ScenarioFile.Save(statePath, session.CaptureScenario(0));
            Require(before.SequenceEqual(File.ReadAllBytes(statePath)), "Both clocks, cameras, selections and delta-T must survive.");
            Require(identities.SequenceEqual(Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity)), "Callback identities must survive.");
            Require(previousSettings == (File.Exists(settings) ? File.ReadAllText(settings) : null), "The superseded preference request must not be saved.");
            Require(scene.LastAddonDiagnostic is null, "Supersession must not publish an error.");

            Task<bool> pending = scene.ReloadAddOns();
            Require(!pending.IsCompleted, "The real preparation must yield before closing its owner.");
            RemoveChild(scene);
            await Until(() => pending.IsCompleted, "closing during preparation");
            Require(!await pending, "A closed scene must reject pending publication.");
            scene.Free(); scene = null;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            GD.Print("SpaceTangent add-on lifecycle probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally
        {
            ProbeHost.FreeScene(this, scene);
            ProbeHost.Complete(this, exit);
        }
    }

    private Task Until(Func<bool> complete, string operation) =>
        ProbeHost.Until(this, complete, 40, operation + " timed out.");

    private static void Require(bool condition, string message) => ProbeHost.Require(condition, message);
}
