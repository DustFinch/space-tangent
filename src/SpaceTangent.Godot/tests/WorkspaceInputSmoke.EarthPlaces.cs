// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

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
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", query);
            await Click(Result(place.Id));
            await Click(Find<Button>("GoToObject"));
            Require(Session[0].Mode == CameraMode.Surface && Session[0].EarthObserver?.Site == place.ObserverSite,
                $"Go to must open the original Earth observing site {place.EnglishName}.");
        }

        async Task Reload(string caption)
        {
            var addOns = Find<Button>("AddOnsTab");
            if (!addOns.ButtonPressed)
                await Click(addOns);
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns(caption);
        }
    }
}
