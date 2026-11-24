// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Addons;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class AddonBindingFailureProbe : Node
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
            await ProbeHost.Until(this, () => scene.InitializationFinished, 60, "Binding probe startup timed out.");
            var session = scene.Session ?? throw new InvalidOperationException("Real simulation required.");
            Require(session.World.Satellites.Any(item => item.Id == new ObjectId("norad/26997")), "Real Jason-1 required.");
            Require(scene.PublishedAddons?.Mission is not null, "The real Cassini mission must initially load.");
            session.SetPlaying(0, false); scene.SetMode(0, CameraMode.Satellites);
            if (session.ViewCount == 1) scene.ToggleSplit();
            scene.ShowFullMoon(1); session.Synchronize(false, 0); session.Step(1, 3600);
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            string snapshotPath = Path.ChangeExtension(System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH")!, ".binding.stscene");
            byte[] Snapshot() { ScenarioFile.Save(snapshotPath, session.CaptureScenario(0)); return File.ReadAllBytes(snapshotPath); }
            byte[] observers = Snapshot();
            int[] identities = Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity).ToArray();
            Require(!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_ADDONS_SETTINGS_PATH")), "Isolated preferences required.");
            var file = new LocalAddonSettingsFile();
            foreach (int stage in new[] { -1, 0, 1, 2 })
            {
                var previous = scene.PublishedAddons!;
                bool enable = previous.Mission is null;
                var requested = scene.AppliedAddonSettings.SetDefault(AddonSourceKind.Mission, enable);
                scene.AddonBindingCheckpoint = view => { if (view == stage) throw new InvalidDataException("Injected binding failure."); };
                Require(!await scene.ReloadAddOns(requested), "The injected binding error must reject completion.");
                var published = scene.PublishedAddons!;
                Require(!ReferenceEquals(previous, published) && ReferenceEquals(session.World, published.Decoded.World), "The accepted world must remain the published world.");
                Require(ReferenceEquals(previous, scene.RetiringAddons) && !previous.IsDisposed && !published.IsDisposed,
                    "Partially switched views must keep both generations alive.");
                Require(file.Load().DisabledDefaults.Contains(AddonSourceKind.Mission) == !enable &&
                    scene.AppliedAddonSettings.DisabledDefaults.SequenceEqual(requested.DisabledDefaults), "Disk and memory source preferences must agree after commit.");
                Require(observers.SequenceEqual(Snapshot()) &&
                    identities.SequenceEqual(Enumerable.Range(0, session.ViewCount).Select(session.ViewIdentity)), "Binding failure changed cameras, epochs, delta-T or identities.");
                Require(!await scene.ReloadAddOns() && ReferenceEquals(scene.PublishedAddons, published) &&
                    ReferenceEquals(scene.RetiringAddons, previous), "Repeated binding failure must not accumulate generations.");
                scene.AddonBindingCheckpoint = null;
                Require(await scene.ReloadAddOns() && previous.IsDisposed && scene.RetiringAddons is null,
                    "A repaired binding must release the previous generation.");
                Require(observers.SequenceEqual(Snapshot()), "Recovery changed observers.");
                var toggled = scene.AppliedAddonSettings with { HipsOffline = !scene.AppliedAddonSettings.HipsOffline };
                Require(await scene.ReloadAddOns(toggled) && file.Load().DisabledDefaults.Contains(AddonSourceKind.Mission) == !enable,
                    "The next panel change must start from committed settings.");
            }
            var old = scene.PublishedAddons!;
            scene.AddonBindingCheckpoint = _ => throw new InvalidDataException("Injected shutdown binding failure.");
            Require(!await scene.ReloadAddOns(scene.AppliedAddonSettings.SetDefault(AddonSourceKind.Mission, old.Mission is null)), "Prepare a pending retirement for shutdown.");
            var active = scene.PublishedAddons!;
            RemoveChild(scene);
            Require(old.IsDisposed && active.IsDisposed && scene.RetiringAddons is null, "Scene shutdown must close both generations.");
            Require(!await scene.ReloadAddOns(), "A closed scene must quietly reject late reload callbacks.");
            await scene.CheckLateObserverCallbacks();
            scene.Free(); scene = null;
            GD.Print("SpaceTangent add-on binding failure probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        finally { ProbeHost.FreeScene(this, scene); ProbeHost.Complete(this, exit); }
    }
    private static void Require(bool value, string message) => ProbeHost.Require(value, message);
}
