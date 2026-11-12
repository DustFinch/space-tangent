// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;
using System.Text.Json.Nodes;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke : Node
{
    private static readonly ObjectId Jason = new("norad/26997");
    private SolarSystemScene _scene = null!;
    private SimulationSession Session => _scene.Session!;
    private SceneTree Tree => GetTree();

    public override async void _Ready()
    {
        const string settingsVariable = "SPACETANGENT_SETTINGS_PATH";
        const string scienceVariable = "SPACETANGENT_SCIENCE_ROOT";
        const string addonSettingsVariable = "SPACETANGENT_ADDONS_SETTINGS_PATH";
        const string bookmarkVariable = "SPACETANGENT_BOOKMARKS_PATH";
        string? oldBookmarks = System.Environment.GetEnvironmentVariable(bookmarkVariable);
        string? oldSettings = System.Environment.GetEnvironmentVariable(settingsVariable);
        string? oldAddonSettings = System.Environment.GetEnvironmentVariable(addonSettingsVariable);
        string? science = System.Environment.GetEnvironmentVariable(scienceVariable);
        int exitCode = 1;
        DirectoryInfo? scratch = null;
        try
        {
            Require(!string.IsNullOrWhiteSpace(science) && Directory.Exists(science),
                "Set SPACETANGENT_SCIENCE_ROOT to the original science add-ons.");
            scratch = Directory.CreateTempSubdirectory("SpaceTangent-ui-");
            string settings = Path.Combine(scratch.FullName, "delta-t.txt");
            string addonSettings = Path.Combine(scratch.FullName, "local-addons.json");
            string bookmarks = Path.Combine(scratch.FullName, "bookmarks.json");
            System.Environment.SetEnvironmentVariable(bookmarkVariable, bookmarks);
            System.Environment.SetEnvironmentVariable(settingsVariable, settings);
            System.Environment.SetEnvironmentVariable(addonSettingsVariable, addonSettings);

            Tree.Root.Size = new(1600, 900);
            Tree.Root.GuiEmbedSubwindows = true;
            _scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(_scene);
            await Until(() => _scene.InitializationFinished, "Simulation startup", 25);
            Require(_scene.Session is not null && Session.World.Satellites.Any(satellite => satellite.Id == Jason),
                "DE440 and the original Jason-1 add-on must load.");
            Tree.Root.NotifyMouseEntered();

            await ExerciseStartupFlight(scratch.FullName);
            await ExerciseNestedViews(scratch.FullName);
            await ExerciseViews();
            var expected = Session.CaptureScenario(1);
            Require(expected.ActiveView == 1 && !expected.Synchronized &&
                    expected.Views[0].Navigation is { Tracking: true } &&
                    expected.Views[1].Mode == CameraMode.Surface,
                "The capture must have Jason-1 tracking and an independent full-Moon view.");
            var copiedScience = Path.Combine(scratch.FullName, "science");
            CopyDirectory(science!, copiedScience);
            var oldWorld = Session.World;
            System.Environment.SetEnvironmentVariable(scienceVariable, copiedScience);
            await Click(Find<Button>("AddOnsTab"));
            await Click(Find<Button>("ReloadAddOns"));
            await Until(() => !ReferenceEquals(oldWorld, Session.World), "Republishing the add-on world", 20);
            Require(Find<Label>("WorkspaceStatus").Text == "Add-ons reloaded.",
                "The reload control must report success.");
            RequireScenario(expected, Session.CaptureScenario(1), "Reloading add-ons");
            Require(File.ReadAllText(settings).Trim() == nameof(DeltaTModel.MorrisonStephenson2004),
                "The ΔT selection must persist to disk after reload.");
            GD.Print("UI: reloaded original add-ons and preserved both view states and ΔT");

            const string filename = "jason-and-moon.stscene";
            string scenario = Path.Combine(scratch.FullName, filename);
            await UseScenarioDialog("SaveScenario", 1, scratch.FullName, filename);
            await Until(() => File.Exists(scenario), "Saving through the file dialog");
            RequireScenario(expected, ScenarioFile.Load(scenario), "Saving through the file dialog");
            await Click(Find<Button>("SplitViews"));
            Require(Session.ViewCount == 1, "Closing the second view must change the current state.");
            await ChooseDeltaT(DeltaTModel.EspenakMeeus2006);
            await UseScenarioDialog("OpenScenario", 0, scratch.FullName, filename);
            await Until(() => Find<Label>("WorkspaceStatus").Text == "Scenario opened.", "Opening the saved scenario");
            RequireScenario(expected, Session.CaptureScenario(1), "Reopening the saved scenario");
            Require(_scene.Renderer(0).NavigationCameraCurrent && _scene.Renderer(0).SatelliteMesh(Jason).Visible,
                "The tracked Jason-1 camera and mesh must be restored.");
            Require(Find<OptionButton>("DeltaTModel").GetSelectedId() == (int)expected.DeltaT,
                "The ΔT control must reflect the restored scenario.");
            GD.Print("UI: saved, changed and reopened the scenario with both cameras and ΔT restored");

            var oldScene = JsonNode.Parse(File.ReadAllText(scenario))!.AsObject();
            oldScene["version"] = 2;
            foreach (var view in oldScene["views"]!.AsArray())
            {
                view!.AsObject().Remove("earthObserver");
                view.AsObject().Remove("sky");
            }
            const string oldFilename = "version-two.stscene";
            File.WriteAllText(Path.Combine(scratch.FullName, oldFilename), oldScene.ToJsonString());
            await UseScenarioDialog("OpenScenario", 0, scratch.FullName, oldFilename);
            Require(Session[1].EarthObserver is null && Session[1].Mode == CameraMode.Surface &&
                    Session[0].Navigation == expected.Views[0].Navigation,
                "A version 2 scenario must restore tracking and keep the original default Earth camera.");
            await ExerciseObserverSites(scratch.FullName, science!);
            await ExerciseBrightSky(scratch.FullName);
            string? mission = System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_CATALOG");
            if (!string.IsNullOrWhiteSpace(mission))
                await ExerciseLocalAddOns(scratch.FullName, addonSettings, science!, mission);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PLANETARY_SMOKE") == "1")
                await ExercisePlanetNavigation(scratch.FullName);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_SKY_SMOKE") == "1")
                await ExerciseSpatialSky(scratch.FullName);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_VIEW_FRAME_SMOKE") == "1")
            {
                string restore = Path.Combine(scratch.FullName, "before-view-frames.stscene");
                ScenarioFile.Save(restore, Session.CaptureScenario(0));
                await ExerciseViewFrames(scratch.FullName, bookmarks);
                Require(_scene.LoadScenario(restore), "The previous working scene must reopen after frame acceptance.");
            }
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK") is { Length: > 0 } bodyPack)
                await ExerciseSscBodies(scratch.FullName, bodyPack);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SAMPLED_MISSIONS_SMOKE") is { Length: > 0 } missions)
                await ExerciseSampledMissions(scratch.FullName, missions);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TLE_SMOKE") is { Length: > 0 } satellites)
                await ExerciseLocalTle(scratch.FullName, satellites);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TOUR_SOURCES") is { Length: > 0 } tours)
                await ExerciseOriginalTours(scratch.FullName, tours);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES")))
                await ExerciseEarthOrbit();
            await ExerciseShipFlight();
            await ExerciseSurfaceSites(scratch.FullName);
            await ExerciseMeasurements(scratch.FullName);
            await ExerciseReferenceMarks(scratch.FullName);
            await ExerciseClockTools(scratch.FullName);
            await ExerciseObservationEvents(scratch.FullName);
            await ExerciseEarthPlaces(scratch.FullName);
            await ExerciseObserverNight(scratch.FullName);
            await ExerciseMeteors(scratch.FullName);
            GD.Print($"SpaceTangent workspace input smoke passed ({DisplayServer.GetName()})");
            exitCode = 0;
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(settingsVariable, oldSettings);
            System.Environment.SetEnvironmentVariable(addonSettingsVariable, oldAddonSettings);
            System.Environment.SetEnvironmentVariable(bookmarkVariable, oldBookmarks);
            System.Environment.SetEnvironmentVariable(scienceVariable, science);
            scratch?.Delete(recursive: true);
            Tree.Quit(exitCode);
        }
    }

    private async Task ExerciseObserverSites(string directory, string originalScience)
    {
        var epochLeft = Session[0].Epoch;
        var epochRight = Session[1].Epoch;
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        await Click(Find<Button>("LocationTab"));
        var site = Find<OptionButton>("ObserverSite");
        await Click(site);
        await ChooseMenuItem(site.GetPopup(), 2);
        Require(Session[0].EarthObserver?.Site == EarthObserverCamera.Tromso.Site, "Selecting Tromsø must set the left site.");

        var preset = Session[0];
        await FillField("ObserverLatitude", "91");
        await Click(Find<Button>("ApplyObserver"));
        Require(Session[0] == preset && Find<Label>("WorkspaceStatus").Text.Contains("latitude"),
            "An out-of-range site must leave the view unchanged and explain the error.");
        await FillField("ObserverLatitude", "69.6500");
        await FillField("ObserverLongitude", "18.9500");
        await FillField("ObserverHeight", "0.025");
        await FillField("ObserverAzimuth", "180");
        await FillField("ObserverElevation", "0");
        await Click(Find<Button>("ApplyObserver"));
        await Click(Find<CheckButton>("HorizonGrid"));
        await Click(Find<CheckButton>("CardinalPoints"));
        EarthObserverCamera left = Session[0].EarthObserver!.Value;
        Require(left.Site == new SpaceTangent.Core.Observing.EarthSite(69.65, 18.95, .025) &&
                left.AzimuthDegrees == 180 && left.ElevationDegrees == 0 &&
                left.HorizonGrid && left.CardinalPoints && Session[0].Mode == CameraMode.Surface &&
                Session[0].Epoch == epochLeft && Session[1].Epoch == epochRight && Session[1].EarthObserver is null,
            "The editable direction and overlays must affect only the left view and preserve both clocks.");

        await Click(Find<SubViewportContainer>("ViewCanvas1"));
        await Click(Find<Button>("ShowFullMoon"));
        Require(Session[1].EarthObserver == EarthObserverCamera.Moscow && Session[0].EarthObserver == left,
            "The full-Moon preset must restore Moscow and its original direction in the right view only.");
        var search = Find<Button>("SearchTab");
        if (!search.ButtonPressed)
            await Click(search);
        await FillField("ObjectQuery", "Moon");
        var results = Find<Container>("ObjectResults");
        var moon = results.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>()
            .Single(button => button.TooltipText == "sol/moon");
        await Click(moon);
        await Click(Find<Button>("EphemeridesTab"));
        await Until(() => Find<Label>("HorizontalCoordinates").Text.Contains("Azimuth"),
            "Showing the selected Moon's horizontal coordinates");
        Require(Find<Label>("CivilTwilight").Text.Contains("Morning") && Find<Label>("CivilTwilight").Text.Contains("Evening"),
            "The Moscow ephemerides panel must show both civil twilight intervals.");

        var expected = Session.CaptureScenario(1);
        var oldWorld = Session.World;
        System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", originalScience);
        await Click(Find<Button>("AddOnsTab"));
        await Click(Find<Button>("ReloadAddOns"));
        await Until(() => !ReferenceEquals(oldWorld, Session.World), "Reloading with two Earth observers", 20);
        RequireScenario(expected, Session.CaptureScenario(1), "Reloading with two Earth observers");

        const string filename = "earth-observers.stscene";
        string path = Path.Combine(directory, filename);
        await UseScenarioDialog("SaveScenario", 1, directory, filename);
        RequireScenario(expected, ScenarioFile.Load(path), "Saving both Earth observers");
        await Click(Find<Button>("SplitViews"));
        await UseScenarioDialog("OpenScenario", 0, directory, filename);
        RequireScenario(expected, Session.CaptureScenario(1), "Reopening both Earth observers");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_OBSERVER_SCENE") is { Length: > 0 } capture)
            File.Copy(path, capture, overwrite: true);
        GD.Print("UI: verified Moscow and Tromsø, horizontal coordinates, twilight, and both observers after reload and reopen");
    }

    private async Task FillField(string name, string value)
    {
        var field = Find<LineEdit>(name);
        await Click(field);
        await KeyPress(Key.A, ctrl: true);
        await KeyPress(Key.Backspace);
        await TypeText(value);
        Require(field.Text == value, $"The {name} field received '{field.Text}' instead of '{value}' (focus: {field.HasFocus()}).");
    }

    private async Task ExerciseStartupFlight(string directory)
    {
        var earth = SolarSystemSample.EarthId;
        var start = Session[0];
        var renderer = _scene.Renderer(0);
        var canvas = Find<SubViewportContainer>("ViewCanvas0");
        Require(start.Selection == earth && start.Navigation is { Tracking: true } && renderer.NavigationCameraCurrent,
            "The first view must start near Earth with a live perspective camera.");
        string path = Path.Combine(directory, "start.stscene");
        ScenarioFile.Save(path, Session.CaptureScenario(0));

        var search = Find<Button>("SearchTab");
        if (!search.ButtonPressed)
            await Click(search);
        await FillField("ObjectQuery", "Moon");
        await Click(Result(SolarSystemSample.MoonId));
        await Click(canvas);
        Require(Session[0].Selection == earth && Session[0].Navigation == start.Navigation,
            "Clicking Earth in the scene must select it without moving the observer.");

        Window root = Tree.Root;
        Vector2 center = root.GetScreenTransform().AffineInverse() * (canvas.GetScreenTransform() * (canvas.Size / 2));
        using (var wheel = new InputEventMouseButton
        {
            Position = center, GlobalPosition = center, ButtonIndex = MouseButton.WheelDown, Pressed = true
        })
        {
            root.PushInput(wheel, inLocalCoords: true);
            wheel.Pressed = false;
            root.PushInput(wheel, inLocalCoords: true);
        }
        Require(Session[0].Navigation!.Value.RangeKilometers > start.Navigation!.Value.RangeKilometers &&
                renderer.NavigationCameraCurrent, "Zooming away must keep the perspective camera live.");

        await FillField("ObjectQuery", "Saturn");
        await Click(Result(PlanetCatalogue.SaturnId));
        await Click(Find<Button>("GoToObject"));
        Require(Session.IsFlying(0) && renderer.NavigationCameraCurrent, "Flying to Saturn must start in the existing perspective view.");
        await Until(() => !Session.IsFlying(0), "Flying from Earth to Saturn");
        await FillField("ObjectQuery", "Earth");
        await Click(Result(earth));
        await Click(canvas);
        Require(Session[0].Selection == PlanetCatalogue.SaturnId && Session[0].Navigation?.Target == PlanetCatalogue.SaturnId &&
                Session[0].Epoch == start.Epoch && ReferenceEquals(renderer, _scene.Renderer(0)),
            "Saturn must be pickable in the same view and epoch after the flight.");
        await FillField("ObjectQuery", "");
        Require(_scene.LoadScenario(path), "The original startup pose must restore after the flight.");
        GD.Print("UI: launched at Earth, selected both globes in the viewport, and flew to Saturn in one perspective view");
    }

    private async Task ExerciseNestedViews(string directory)
    {
        string original = Path.Combine(directory, "before-nested.stscene");
        Require(_scene.SaveScenario(original), "Save the original workspace before testing nested views.");
        await KeyPress(Key.U, ctrl: true);
        await Click(Find<SubViewportContainer>("ViewCanvas1"));
        await KeyPress(Key.R, ctrl: true);
        Require(Session.ViewCount == 3 && Session.Layout.Second is { Direction: ViewSplit.Stacked, Second.View: 2 },
            $"Ctrl+R must split the active right-hand observer into two stacked views. Layout: {Session.Layout}.");
        var divider = Find<SplitContainer>("ViewDivider1");
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        var dragger = divider.GetDragAreaControls().Single();
        Window root = Tree.Root;
        Vector2 point = root.GetScreenTransform().AffineInverse() * (dragger.GetScreenTransform() * (dragger.Size / 2));
        Vector2 movement = new(0, 32);
        using var enter = new InputEventMouseMotion { Position = point, GlobalPosition = point };
        root.PushInput(enter, inLocalCoords: true);
        using var down = MouseButtonAt(point, true);
        root.PushInput(down, inLocalCoords: true);
        using var drag = new InputEventMouseMotion
        {
            Position = point + movement, GlobalPosition = point + movement, Relative = movement, ButtonMask = MouseButtonMask.Left
        };
        root.PushInput(drag, inLocalCoords: true);
        using var up = MouseButtonAt(point + movement, false);
        root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        Require(Session.Layout.Second!.FirstShare > .5, "Moving the nested divider must update the saved split ratio.");

        await Click(Find<Button>("TimeTab"));
        await Click(Find<CheckButton>("SyncViewTime"));
        var clock = Session[0].Epoch;
        await Click(Find<Button>("AdvanceHour2"));
        Require(!Session.Synchronized && Session[0].Epoch == clock && Session[1].Epoch == clock &&
            Session[2].Epoch == clock.AddSeconds(3600), "The nested clock must step without moving either sibling.");
        var expected = Session.CaptureScenario(2);
        string path = Path.Combine(directory, "nested-views.stscene");
        Require(_scene.SaveScenario(path), "A three-view workspace must save its layout.");
        RequireScenario(expected, ScenarioFile.Load(path), "Reading the saved nested layout");

        var scriptFile = Path.Combine(directory, "nested-clock.lua");
        File.WriteAllText(scriptFile, "wait(30)");
        var survivingScript = LuaSessionScript.Load(scriptFile, Session, 1, hasSurface: true, _ => { });
        var removedScript = LuaSessionScript.Load(scriptFile, Session, 2, hasSurface: true, _ => { });
        await KeyPress(Key.Delete);
        Require(Session.ViewCount == 2 && Session[0] == expected.Views[0] && Session[1] == expected.Views[1] &&
            removedScript.View == -1 && survivingScript.View == 1,
            "Closing a view must preserve siblings and invalidate the closed Lua observer identity.");
        Require(_scene.LoadScenario(path), "The nested workspace must reopen from disk.");
        RequireScenario(expected, Session.CaptureScenario(2), "Restoring the nested workspace");
        var invalid = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        invalid["layout"]!["second"]!["second"]!["view"] = 0;
        string bad = Path.Combine(directory, "duplicate-view.stscene");
        File.WriteAllText(bad, invalid.ToJsonString());
        Require(!_scene.LoadScenario(bad), "A layout that duplicates a view and omits another must be rejected.");
        RequireScenario(expected, Session.CaptureScenario(2), "Rejecting a duplicate leaf");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_NESTED_SCENE") is { Length: > 0 } capture)
            File.Copy(path, capture, overwrite: true);
        var third = Find<SubViewportContainer>("ViewCanvas2");
        var second = Find<SubViewportContainer>("ViewCanvas1");
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        Require(third.GlobalPosition.Y > second.GlobalPosition.Y && second.Size.Y > third.Size.Y,
            "The two restored right-hand observers must be stacked with the saved size order.");
        await KeyPress(Key.D, ctrl: true);
        Require(Session.ViewCount == 1 && Session[0] == expected.Views[2],
            "Collapsing the layout must keep the active observer and its clock.");
        Require(_scene.LoadScenario(original), "Restore the original workspace after the layout checks.");
        GD.Print("UI: restored a nested three-view layout, divider, independent clock and Lua observer identities");
    }

    private async Task ExerciseViews()
    {
        await KeyPress(Key.U, ctrl: true);
        Require(Session.ViewCount == 2, "Ctrl+U must create two views.");
        var time = Find<Button>("TimeTab");
        if (!time.ButtonPressed)
            await Click(time);
        await Click(Find<CheckButton>("SyncViewTime"));
        Require(!Session.Synchronized, "The time controls must allow independent clocks.");

        await Click(Find<Button>("SearchTab"));
        var query = Find<LineEdit>("ObjectQuery");
        await Click(query);
        Require(query.HasFocus(), "Clicking the search field must give it internal GUI focus.");
        await TypeText("26997");
        Require(query.Text == "26997", "The search field must receive the typed catalogue ID.");
        var results = Find<Container>("ObjectResults");
        var jason = results.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>()
            .Single(button => button.TooltipText == Jason.Value);
        await Click(jason);
        Require(Session[0].Selection == Jason, "Clicking the filtered result must select Jason-1 in the left view.");
        var goTo = Find<Button>("GoToObject");
        await Click(goTo);
        Require(Session.IsFlying(0), "Clicking Go to must start the flight.");
        await KeyPress(Key.Escape);
        Require(!Session.IsFlying(0) && Find<Label>("WorkspaceStatus").Text == "Flight stopped.",
            "Escape must cancel the flight and report that it stopped.");
        await Click(goTo);
        Require(Session.IsFlying(0), "The flight must be restartable after cancellation.");
        await Until(() => !Session.IsFlying(0), "Flying to Jason-1");
        Require(Session[0].Navigation is { Target: { } target } && target == Jason &&
                _scene.Renderer(0).NavigationCameraCurrent, "The flight must reach Jason-1.");
        await Click(Find<Button>("TrackObject"));
        var tracked = Session[0].Navigation;
        Require(tracked is { Tracking: true }, "Clicking Track must lock the camera onto Jason-1.");

        var right = Find<SubViewportContainer>("ViewCanvas1");
        await KeyPress(Key.Tab);
        await Click(Find<Button>("LocationTab"));
        await Click(Find<Button>("ShowFullMoon"));
        Require(Session[1].Mode == CameraMode.Surface && Session[1].Epoch == SurfaceProjection.FullMoonEpoch,
            "The right view must show the full-Moon preset.");
        var moon = Session[1];
        var epoch = Session[0].Epoch;
        await Click(Find<Button>("AdvanceHour0"));
        Require(Session[0].Epoch == epoch.AddSeconds(3600) && Session[1] == moon &&
                Session[0].Navigation is { Tracking: true } moved && moved.AnchorKilometers != tracked!.Value.AnchorKilometers,
            "The left hour step must track Jason-1 while preserving the right camera and time.");
        GD.Print("UI: split and switched by shortcut, canceled and completed the flight to Jason-1, and tracked it in an independent view");

        await Click(Find<Button>("TimeTab"));
        var sync = Find<CheckButton>("SyncViewTime");
        await Click(sync);
        Require(Session.Synchronized && Session[1].Epoch == Session[0].Epoch, "The sync toggle must join both clocks.");
        epoch = Session[0].Epoch;
        await Click(Find<Button>("AdvanceHour0"));
        Require(Session[0].Epoch == epoch.AddSeconds(3600) && Session[1].Epoch == Session[0].Epoch,
            "A step with sync enabled must advance both views.");
        await Click(sync);
        await Click(right);
        await Click(Find<Button>("LocationTab"));
        await Click(Find<Button>("ShowFullMoon"));
        Require(!Session.Synchronized && Session[0].Epoch != Session[1].Epoch &&
                Session[1].Epoch == SurfaceProjection.FullMoonEpoch, "Disabling sync must allow restoring the full Moon independently.");
        await ChooseDeltaT(DeltaTModel.MorrisonStephenson2004);
        GD.Print("UI: synchronized both views, restored independent epochs and selected Morrison–Stephenson ΔT");
    }

    private async Task ChooseDeltaT(DeltaTModel model)
    {
        var tab = Find<Button>("TimeTab");
        if (!tab.ButtonPressed)
            await Click(tab);
        var option = Find<OptionButton>("DeltaTModel");
        await Click(option);
        await ChooseMenuItem(option.GetPopup(), option.GetItemIndex((int)model));
        Require(Session.DeltaT == model && option.GetSelectedId() == (int)model, "The ΔT menu must apply the selected model.");
    }

    private async Task UseScenarioDialog(string dialogName, int menuIndex, string directory, string filename)
    {
        var tab = Find<Button>("BookmarksTab");
        if (!tab.ButtonPressed)
            await Click(tab);
        var menu = Find<MenuButton>("ScenarioMenu");
        var dialog = _scene.GetNode<FileDialog>(dialogName);
        // Supply the fixture directory without depending on a machine's default path.
        dialog.CurrentDir = directory;
        await Click(menu);
        await ChooseMenuItem(menu.GetPopup(), menuIndex);
        await Until(() => dialog.Visible, $"Opening {dialogName}");
        var name = dialog.GetLineEdit();
        await Click(name);
        Require(name.HasFocus(), $"The {dialogName} filename field must receive focus.");
        await KeyPress(Key.A, ctrl: true);
        await TypeText(filename);
        Require(name.Text == filename, $"The {dialogName} filename field must receive the full filename.");
        await KeyPress(Key.Enter);
    }

    private async Task ChooseMenuItem(PopupMenu menu, int index)
    {
        Require(menu.Visible && index >= 0, "The requested popup item must be visible.");
        for (int attempt = 0; menu.GetFocusedItem() != index && attempt < menu.ItemCount; attempt++)
            await KeyPress(Key.Down);
        Require(menu.GetFocusedItem() == index, $"Keyboard navigation did not reach popup item {index}.");
        await KeyPress(Key.Enter);
    }

    private T Find<T>(string name) where T : Node =>
        _scene.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name} {name}.");

    private async Task Click(Control control)
    {
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        Require(control.IsVisibleInTree() && control.Size.X > 0 && control.Size.Y > 0,
            $"Cannot click hidden or empty control {control.Name}.");
        Window root = Tree.Root;
        Vector2 point = root.GetScreenTransform().AffineInverse() *
            (control.GetScreenTransform() * (control.Size / 2));
        using var move = new InputEventMouseMotion { Position = point, GlobalPosition = point };
        root.PushInput(move, inLocalCoords: true);
        using var down = MouseButtonAt(point, true);
        root.PushInput(down, inLocalCoords: true);
        using var up = MouseButtonAt(point, false);
        root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private static InputEventMouseButton MouseButtonAt(Vector2 point, bool down) => new()
    {
        Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left,
        ButtonMask = down ? MouseButtonMask.Left : 0, Pressed = down
    };

    private async Task TypeText(string text)
    {
        foreach (char character in text)
            await KeyPress((Key)char.ToUpperInvariant(character), character);
    }

    private async Task KeyPress(Key key, uint unicode = 0, bool ctrl = false, bool shift = false, bool alt = false)
    {
        using var down = new InputEventKey { Keycode = key, Unicode = unicode, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt, Pressed = true };
        Tree.Root.PushInput(down, inLocalCoords: true);
        using var up = new InputEventKey { Keycode = key, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt };
        Tree.Root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private async Task Until(Func<bool> ready, string description, int timeoutSeconds = 6)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            Require(stopwatch.Elapsed.TotalSeconds < timeoutSeconds, $"{description} timed out.");
            await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void RequireScenario(SimulationScenario expected, SimulationScenario actual, string action) =>
        Require(expected.Views.SequenceEqual(actual.Views) && expected.DeltaT == actual.DeltaT &&
                expected.Synchronized == actual.Synchronized && expected.ClockOwner == actual.ClockOwner &&
                expected.ActiveView == actual.ActiveView && expected.SplitLayout == actual.SplitLayout && expected.Layout == actual.Layout,
            $"{action} must preserve both cameras, epochs, selections, sync settings and ΔT.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
