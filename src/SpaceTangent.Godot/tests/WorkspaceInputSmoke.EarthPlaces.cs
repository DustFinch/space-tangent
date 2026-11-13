// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseEarthPlaces(string directory)
    {
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_EARTH_PLACES")))
            return;
        const string savedVariable = "SPACETANGENT_SAVED_EARTH_PLACES";
        string? prior = System.Environment.GetEnvironmentVariable(savedVariable);
        string restore = Path.Combine(directory, "before-earth-places.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        string path = Path.Combine(directory, "earth-places.stscene");
        string savedFile = Path.Combine(directory, "saved sites.tsv");
        var left = new ObserverView(Session[0].Epoch, CameraMode.Solar, null, false);
        var right = left with { Epoch = left.Epoch.AddSeconds(3600) };
        ScenarioFile.Save(path, new([left, right], false, 0, 0, Session.DeltaT));
        Require(_scene.LoadScenario(path), "The independent site test fixture must open.");
        try
        {
            var archive = Session.World.EarthPlaces.Places;
            Require(archive.Count >= 6822, "The complete archived observing list must be available.");
            var observatory = archive.Single(place => place.EnglishName == "Paranal Observatory");
            Require(Session.World.Catalogue.Search("Москва").Any(place => place.Name == "Moscow"),
                "The original localized site name must be searchable.");
            await OpenPlace("Paranal Observatory", observatory);
            Require(Session[0].EarthObserver?.Conditions is { Bortle: 1 },
                "The archived Paranal Bortle class must apply on site selection.");
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
            await KeyPress(Key.G, ctrl: true);
            Require(Session[0].EarthObserver?.Conditions is { Bortle: 2 } && Session[1] == right,
                "The class shortcut must change only the active observer.");
            await KeyPress(Key.G, ctrl: true, alt: true);
            Require(Session[0].EarthObserver?.Conditions is null, "The ground sky shortcut must restore the geometric sky.");
            Require(Session[1] == right, "Choosing the observatory must affect only the active view.");

            File.WriteAllText(savedFile, "Home Observatory\t51.5\t-0.125\t30\t0\tEurope/London\n");
            System.Environment.SetEnvironmentVariable(savedVariable, savedFile);
            await Reload("Discovering the archived custom site file");
            var saved = Session.World.EarthPlaces.Places.Single(place => place.Name == "Home Observatory");
            await OpenPlace(saved.EnglishName, saved);
            await Click(Find<Button>("LocationTab"));
            var map = Find<EarthSiteMap>("EarthSiteMap");
            Vector2 paranal = map.Project(observatory.LatitudeDegrees, observatory.LongitudeDegrees);
            var screen = map.GetGlobalRect().Position + paranal;
            using (var down = MouseButtonAt(screen, true))
                Tree.Root.PushInput(down, inLocalCoords: true);
            using (var up = MouseButtonAt(screen, false))
                Tree.Root.PushInput(up, inLocalCoords: true);
            await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            Require(Session[0].EarthObserver?.Site == observatory.ObserverSite && Session[1] == right,
                "Clicking Paranal on the map must apply the original coordinates to only the active view.");
            await FillField("SavedSiteName", "Параналь · карта");
            await Click(Find<Button>("SaveEarthSite"));
            await Until(() => Session.World.EarthPlaces.Places.Any(place => place.Name == "Параналь · карта"),
                "Reloading the site saved from the map", 30);
            Require(EarthPlaceCatalogue.ParseSavedText(File.ReadAllText(savedFile)).Any(place =>
                place.Name == "Параналь · карта" && place.ObserverSite == observatory.ObserverSite),
                "The named site must survive reading the saved TSV from disk.");
            Vector2 firstScale = map.Project(0, 30) - map.Project(0, 0);
            Vector2 center = map.GetGlobalRect().GetCenter();
            using (var wheel = new InputEventMouseButton { Position = center, GlobalPosition = center, Pressed = true,
                       ButtonIndex = MouseButton.WheelUp })
                Tree.Root.PushInput(wheel, inLocalCoords: true);
            await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
            Require((map.Project(0, 30) - map.Project(0, 0)).Length() > firstScale.Length() &&
                Session[0].EarthObserver?.Site == observatory.ObserverSite, "Zooming the map must leave the ground observer at Paranal.");
            Find<LineEdit>("SavedSiteName").GrabFocus();
            await KeyPress(Key.A, ctrl: true);
            await TypeText(saved.Name);
            Require(Find<LineEdit>("SavedSiteName").Text == saved.Name, "The name field must accept the site being updated.");
            await KeyPress(Key.Enter);
            await Until(() => Session.World.EarthPlaces.Find(saved.Id)?.ObserverSite == observatory.ObserverSite,
                "Updating a site by name", 30);
            Require(Session.World.EarthPlaces.Find(saved.Id)?.TimeZone == "Europe/London",
                "Updating coordinates must preserve the existing time zone.");
            GD.Print("UI: selected and zoomed to Paranal on the map, saved a new site and updated an existing site by name.");
            File.WriteAllText(savedFile, "Home Observatory\t51.5\t-0.125\t30\t0\tEurope/London\n");
            Require(await _scene.ReloadAddOns(), "Restoring the original custom site before migration checks.");
            await OpenPlace(saved.EnglishName, saved);
            var accepted = Session.CaptureScenario(0);
            ScenarioFile.Save(path, accepted);
            RequireScenario(accepted, ScenarioFile.Load(path), "Saving a named local observing site");
            File.WriteAllText(savedFile, "Home Observatory\t51.6\t-0.125\t30\t0\tEurope/London\n");
            await Reload("Reading the updated local observing site");
            RequireScenario(accepted, Session.CaptureScenario(0), "Reloading a changed saved site list");
            var changed = Session.World.EarthPlaces.Find(saved.Id);
            Require(changed is { LatitudeDegrees: 51.6 }, "The saved site's stable ID must resolve to its edited coordinates.");
            await OpenPlace(saved.EnglishName, changed!);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SETTINGS") is { Length: > 0 } archiveProfile)
            {
                var profile = JsonNode.Parse(File.ReadAllText(archiveProfile))!.AsObject();
                profile["SavedSites"] = "Home Observatory\t52\t-0.125\t30\t0\tEurope/London\n" +
                    "Площадка Орион\t55.1\t37.2\t90\t3\tEurope/Moscow\ninvalid\t1000\t0\t0\t0\n";
                string copy = Path.Combine(directory, "Архивные площадки.json");
                File.WriteAllText(copy, profile.ToJsonString());
                await _scene.ImportStartupSettings(copy);
                var imported = Session.World.EarthPlaces.Places.Single(place => place.Name == "Площадка Орион");
                Require(imported.ObserverSite == new EarthSite(55.1, 37.2, .09) && imported.TimeZone == "Europe/Moscow" &&
                    Session.World.EarthPlaces.Find(saved.Id) is { LatitudeDegrees: 51.6 } &&
                    Session.World.EarthPlaces.Places.All(place => place.Name != "invalid") && Session[1] == right,
                    "Archived sites must merge without changing a duplicate, an invalid entry or the second observer.");
                byte[] once = File.ReadAllBytes(savedFile);
                await _scene.ImportStartupSettings(copy);
                Require(File.ReadAllBytes(savedFile).SequenceEqual(once), "Repeating a legacy profile import must leave the saved file unchanged.");
                await OpenPlace(imported.EnglishName, imported);
                GD.Print("UI: migrated an archived profile's Cyrillic saved site, preserved a duplicate and the other observer, and reopened it by search");
            }
            GD.Print("UI: found Paranal and the localized Moscow alias, activated a saved site, and kept both views through reload");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(savedVariable, prior);
        }
        await Reload("Restoring the original observer sites");
        Require(_scene.LoadScenario(restore), "The previous workspace must reopen after checking observing sites.");

        async Task OpenPlace(string query, EarthPlace place)
        {
            if (!Find<LineEdit>("ObjectQuery").IsVisibleInTree())
            {
                Tree.Root.GuiGetFocusOwner()?.ReleaseFocus();
                await KeyPress(Key.Enter);
            }
            await FillField("ObjectQuery", query);
            await Click(Result(place.Id));
            await Click(Find<Button>("GoToObject"));
            Require(Session[0].Mode == CameraMode.Surface && Session[0].EarthObserver?.Site == place.ObserverSite,
                $"Go to must open the original Earth observing site {place.EnglishName}.");
        }

        async Task Reload(string caption)
        {
            var addOns = Find<Button>("AddOnsTab");
            if (!Find<Button>("ReloadAddOns").IsVisibleInTree())
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns(caption);
        }
    }
}
