// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseBrightSky(string directory)
    {
        VerifyStarDirections();
        var star = new ObjectId("hip/24608");
        await Click(Find<Button>("LayersTab"));
        var otherSky = Session[0].Sky;
        var stars = Find<CheckButton>("StarsLayer");
        if (stars.ButtonPressed)
            await Click(stars);
        await Click(stars);
        await Click(Find<CheckButton>("StarLabelsLayer"));
        await Click(Find<CheckButton>("EquatorialGridLayer"));
        await Click(Find<CheckButton>("ConstellationLayer"));
        var layers = new SkyLayerSettings(true, true, true, true);
        Require(Session[0].Sky == otherSky && Session[1].Sky == layers,
            "The sky layers must be independent in split view.");

        await Click(Find<Button>("SearchTab"));
        await FillField("ObjectQuery", "Capella");
        await Click(Result(star), MouseButton.Right);
        var actions = Find<PopupMenu>("ObjectActions");
        Require(Session[1].Selection == star && actions.GetItemText(0) == UI.WorkspaceText.Choose("Point at", "Навести камеру"),
            "Capella must be selectable and offer pointing for an Earth observer.");
        await ChooseMenuItem(actions, 0);
        Require(Session[1].EarthObserver is { ElevationDegrees: > 0 } && Session[1].Mode == CameraMode.Surface,
            "Pointing at Capella must leave it above the Moscow horizon.");

        await FillField("ObjectQuery", "Moon");
        await Click(Result(new("sol/moon")));
        await Click(Find<SubViewportContainer>("ViewCanvas1"));
        Require(Session[1].Selection == star,
            "Clicking the rendered star must select the same stable object as search.");

        const string filename = "bright-sky.stscene";
        string path = Path.Combine(directory, filename);
        var expected = Session.CaptureScenario(1);
        await UseScenarioDialog("SaveScenario", 1, directory, filename);
        RequireScenario(expected, ScenarioFile.Load(path), "Saving the bright sky layers");
        await Click(Find<Button>("SplitViews"));
        await UseScenarioDialog("OpenScenario", 0, directory, filename);
        RequireScenario(expected, Session.CaptureScenario(1), "Reopening the bright sky layers");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SKY_SCENE") is { Length: > 0 } capture)
            File.Copy(path, capture, overwrite: true);

        await Click(Find<Button>("SpaceMode"));
        Require(Session[1].Mode == CameraMode.Solar && Session[1].Sky == layers,
            "The same per-view layers must remain set when switching to the space camera.");
        const string spaceFilename = "space-sky.stscene";
        await UseScenarioDialog("SaveScenario", 1, directory, spaceFilename);
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SPACE_SKY_SCENE") is { Length: > 0 } spaceCapture)
            File.Copy(Path.Combine(directory, spaceFilename), spaceCapture, overwrite: true);
        await UseScenarioDialog("OpenScenario", 0, directory, "earth-observers.stscene");
        GD.Print("UI: verified published Hipparcos directions, Capella search and sky picking, and independent saved sky layers");
    }

    private Button Result(ObjectId id) => Find<Container>("ObjectResults")
        .FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>()
        .Single(button => button.TooltipText == id.Value);

    private void VerifyStarDirections()
    {
        Require(Session.World.BrightStars.Stars.Count == 5041, "The packaged HYG subset must be complete.");
        // ESA Hipparcos I/239 distributed by NASA HEASARC, at J1991.25. Motions are in mas per Julian year.
        var controls = new (int Hip, double Ra, double Dec, double East, double North)[]
        {
            (32349, 101.28854105, -16.71314306, -546.01, -1223.08),
            (27989, 88.79287161, 7.40703634, 27.33, 10.86),
            (91262, 279.23410832, 38.78299311, 201.02, 287.46),
            (11767, 37.94614689, 89.26413805, 44.22, -11.74)
        };
        const double radians = Math.PI / 180;
        foreach (var (hip, ra, dec, east, north) in controls)
        foreach (double years in new[] { 0, 58.75 })
        {
            var epoch = JulianInstant.FromJulianDate(2448349.0625 + years * 365.25, TimeScale.Tdb);
            var id = new ObjectId(FormattableString.Invariant($"hip/{hip}"));
            var star = Session.World.BrightStars.Find(id);
            Require(star is not null && Session.World.Catalogue.Find(id) is not null,
                $"The renderer and search must use the same object ID for HIP {hip}.");
            double a = (ra + years * east / (3_600_000 * Math.Cos(dec * radians))) * radians;
            double d = (dec + years * north / 3_600_000) * radians;
            var expected = new Vector3d(Math.Cos(d) * Math.Cos(a), Math.Cos(d) * Math.Sin(a), Math.Sin(d));
            double arcseconds = Math.Asin(Vector3d.Cross(expected, star!.DirectionAt(epoch)).Length) / radians * 3600;
            Require(arcseconds < 1, $"HIP {hip} differs from the reference by {arcseconds:F3} arcseconds.");
        }
    }
}
