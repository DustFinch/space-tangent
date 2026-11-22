// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExercisePlaceMemory(string directory)
    {
        string? oldStartup = System.Environment.GetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE");
        string startup = Path.Combine(directory, "remembered-startup.stscene");
        string preferences = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH")! + ".observer-places.json";
        System.Environment.SetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE", startup);
        try
        {
            var left = new ObserverView(SurfaceProjection.FullMoonEpoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
                { EarthObserver = EarthObserverCamera.GreenwichFullMoon };
            var right = new ObserverView(SurfaceProjection.FullMoonEpoch.AddSeconds(3600), CameraMode.Satellites, Jason, false);
            string explicitScene = Path.Combine(directory, "explicit.stscene");
            ScenarioFile.Save(explicitScene, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(explicitScene), "The observing place test needs two independent views.");
            if (!Find<LineEdit>("ObserverLatitude").IsVisibleInTree()) await Click(Find<Button>("LocationTab"));
            await FillField("ObserverLatitude", "-33.9");
            await FillField("ObserverLongitude", "18.4");
            await FillField("ObserverHeight", "-0.02");
            await Click(Find<Button>("ApplyObserver"));
            EarthObserverCamera earth = Session[0].ObserverCamera;
            await ChooseRememberedBody("Moon");
            await FillField("ObserverLatitude", "12");
            await FillField("ObserverLongitude", "34");
            await FillField("ObserverAzimuth", "25");
            await Click(Find<Button>("ApplyObserver"));
            EarthObserverCamera moon = Session[0].ObserverCamera;
            await ChooseRememberedBody("Earth");
            Require(Session[0].ObserverCamera == earth, "Returning from Moon must restore the last Earth place and its direction.");
            await ChooseRememberedBody("Moon");
            Require(Session[0].ObserverCamera == moon, "Returning to Moon must restore its own place.");
            await KeyPress(Key.Tab);
            if (!Find<LineEdit>("ObserverLatitude").IsVisibleInTree()) await Click(Find<Button>("LocationTab"));
            await FillField("ObserverLatitude", "62");
            await FillField("ObserverLongitude", "15");
            await Click(Find<Button>("ApplyObserver"));
            Require(Session[0].ObserverCamera == moon && Session[1].ObserverCamera.Site.LatitudeDegrees == 62,
                "Editing the second view must not change the first view's place.");
            Require(_scene.SaveStartupWorkspace(), "The two views must be saved with stable place keys.");
            var startupState = Session.CaptureScenario(1);
            await FillField("ObserverLatitude", "63");
            await Click(Find<Button>("ApplyObserver"));
            var latest = Session.CaptureScenario(1);
            await Until(() => File.Exists(preferences), "Automatically saving places");
            await RestartPlacesScene();
            Require(Session.ViewCount == 2 && Session[0].ObserverCamera == moon &&
                Session[1].ObserverCamera.Site.LatitudeDegrees == 63,
                "Restart must overlay the newer automatic places on the matching saved view keys.");
            Require(Session[0].Epoch == startupState.Views[0].Epoch && Session[1].Epoch == startupState.Views[1].Epoch &&
                Session.DeltaT == startupState.DeltaT && Session[1].Navigation == startupState.Views[1].Navigation,
                "Automatic places must not overwrite epochs, navigation or Delta-T.");
            Require(Session[0].Places.ViewKey == latest.Views[0].Places.ViewKey &&
                Session[1].Places.ViewKey == latest.Views[1].Places.ViewKey, "Restart must preserve each view's persistence key.");
            await KeyPress(Key.Tab);
            if (!Find<LineEdit>("ObserverLatitude").IsVisibleInTree()) await Click(Find<Button>("LocationTab"));
            await ChooseRememberedBody("Earth");
            Require(Session[0].ObserverCamera == earth, "Inactive Earth history must survive restart.");
            var beforeReload = Session.CaptureScenario(0);
            await Click(Find<Button>("AddOnsTab"));
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Reloading real add-ons with remembered places");
            RequireScenario(beforeReload, Session.CaptureScenario(0), "Reloading with independent remembered places");
            Require(Session[0].Places.ViewKey == beforeReload.Views[0].Places.ViewKey &&
                Session[1].Places.ViewKey == beforeReload.Views[1].Places.ViewKey,
                "Reload must preserve both persistence identities as well as the physical observer states.");
            Require(_scene.LoadScenario(explicitScene), "An explicitly opened scene must take precedence over automatic preferences.");
            Require(Session[0].ObserverCamera == left.EarthObserver, "Explicit Greenwich coordinates must remain explicit.");
            Session.RemoveView(0);
            Session.SetEarthObserver(0, earth);
            File.Delete(startup);
            await RestartPlacesScene();
            Require(Session.ViewCount == 1 && Session[0].ObserverCamera == earth,
                "A fresh workspace must adopt the last remaining view's place without reviving a closed view.");
            string backup = preferences + ".valid";
            File.Move(preferences, backup, overwrite: true);
            Directory.CreateDirectory(preferences);
            var edited = earth with { AzimuthDegrees = 46 };
            Session.SetEarthObserver(0, edited);
            await Until(() => Find<Label>("WorkspaceStatus").Text.StartsWith("Could not save observing places.", StringComparison.Ordinal),
                "Reporting a blocked automatic preference save");
            Require(Session[0].ObserverCamera == edited && File.Exists(backup), "A failed save must preserve the live camera and last valid file.");
            Directory.Delete(preferences);
            await Until(() => File.Exists(preferences), "Retrying an automatic save after the path recovers", 12);
            await RestartPlacesScene(() => File.WriteAllText(preferences, "{broken"));
            Require(Session[0].ObserverCamera == EarthObserverCamera.Default &&
                Find<Label>("WorkspaceStatus").Text.StartsWith("Could not restore observing places.", StringComparison.Ordinal),
                "Corrupt automatic preferences must use the neutral fresh default and report the error.");
            File.Copy(backup, preferences, overwrite: true);
            GD.Print("UI: remembered independent Earth/Moon places across restart, explicit scene, close and real add-on reload; epochs and Delta-T preserved");
        }
        finally { System.Environment.SetEnvironmentVariable("SPACETANGENT_STARTUP_WORKSPACE", oldStartup); }
    }

    private async Task ChooseRememberedBody(string name)
    {
        var bodies = Find<OptionButton>("ObserverBody");
        int index = Enumerable.Range(0, bodies.ItemCount).Single(i => bodies.GetItemText(i) == name);
        await Click(bodies);
        await ChooseMenuItem(bodies.GetPopup(), index);
    }

    private async Task RestartPlacesScene(Action? beforeStart = null)
    {
        RemoveChild(_scene);
        _scene.QueueFree();
        beforeStart?.Invoke();
        _scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
        AddChild(_scene);
        await Until(() => _scene.InitializationFinished, "Restarting the observing place profile", 25);
    }
}
