// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Addons;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class AddonPreferenceFailureProbe : Node
{
    public override async void _Ready()
    {
        SolarSystemScene? scene = null;
        string? settings = null;
        bool createdBlocker = false;
        int exit = 1;
        try
        {
            settings = System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH")
                ?? throw new InvalidOperationException("Use isolated add-on settings for this probe.");
            Require(!File.Exists(settings) && !Directory.Exists(settings), "The probe requires a new isolated settings destination.");
            GetTree().Root.Size = new(1600, 900);
            scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            await Until(() => scene.InitializationFinished, "initialization");
            var session = scene.Session ?? throw new InvalidOperationException("A real simulation is required.");
            Require(session.World.Satellites.Any(item => item.Id == new ObjectId("norad/26997")), "Real Jason-1 must load.");
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            session.SetPlaying(0, false); scene.SetMode(0, CameraMode.Satellites);
            if (session.ViewCount == 1) scene.ToggleSplit();
            scene.ShowFullMoon(1); session.Synchronize(false, 0); session.Step(1, 3600);
            for (int frame = 0; frame < 5; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var world = session.World;
            int[] identities = Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity).ToArray();
            string state = Path.ChangeExtension(settings, ".before.stscene");
            ScenarioFile.Save(state, session.CaptureScenario(0)); byte[] before = File.ReadAllBytes(state);
            Directory.CreateDirectory(settings); createdBlocker = true;
            bool accepted = await scene.ReloadAddOns(LocalAddonSettings.Empty.SetDefault(AddonSourceKind.Science, false));
            Require(!accepted && scene.LastAddonDiagnostic is not null, "An unwritable preference destination must reject the request.");
            Require(ReferenceEquals(world, session.World), "Failed preference persistence changed the live world.");
            ScenarioFile.Save(state, session.CaptureScenario(0));
            Require(before.SequenceEqual(File.ReadAllBytes(state)), "Failed persistence changed clocks, cameras, selection or delta-T.");
            Require(identities.SequenceEqual(Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity)), "Rollback must preserve view callback identities.");
            Directory.Delete(settings); createdBlocker = false;
            Require(await scene.ReloadAddOns(LocalAddonSettings.Empty), "Reload must recover after repairing the destination.");
            Require(session.World.Satellites.Count == 2 && scene.LastAddonDiagnostic is null && File.Exists(settings), "The repaired request must persist and retain science sources.");
            GD.Print("SpaceTangent add-on preference failure probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally
        {
            if (createdBlocker && settings is not null && Directory.Exists(settings)) Directory.Delete(settings);
            ProbeHost.FreeScene(this, scene);
            ProbeHost.Complete(this, exit);
        }
    }

    private Task Until(Func<bool> complete, string operation) =>
        ProbeHost.Until(this, complete, 40, operation + " timed out.");

    private static void Require(bool condition, string message) => ProbeHost.Require(condition, message);
}
