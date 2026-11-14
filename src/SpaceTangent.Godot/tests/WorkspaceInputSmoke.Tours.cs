// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseClassicViews(string directory)
    {
        string restore = Path.Combine(directory, "before-classic-views.stscene");
        Require(_scene.SaveScenario(restore), "Save the original workspace.");
        var left = Session[0] with { Playing = false, FieldOfViewDegrees = 41 };
        var right = left with { Selection = Jason, Epoch = left.Epoch.AddSeconds(7200), FieldOfViewDegrees = 62 };
        string initial = Path.Combine(directory, "classic-views-start.stscene");
        ScenarioFile.Save(initial, new([left, right], false, 0, 1, Session.DeltaT));
        Require(_scene.LoadScenario(initial), "Open independent views with original Jason-1 active.");
        string source = Path.Combine(directory, "classic-views.cel");
        using (var icon = GD.Load<Texture2D>("res://UI/space-tangent.png").GetImage())
            Require(icon.SavePng(Path.Combine(directory, "classic-view.png")) == Error.Ok, "Create a local overlay fixture.");
        File.WriteAllText(source, """
            {
                splitview { view 1 type "h" position 0.35 }
                wait { duration 1 }
                makeactiveview { view 3 }
                set { name "FOV" value 36 }
                overlay { filename "classic-view.png" duration 20 width 64 height 64 }
                wait { duration 1 }
                deleteview { view 2 }
                wait { duration 1 }
                deleteview { view 1 }
                wait { duration 1 }
                splitview { view 1 split "v" position 0.7 }
                makeactiveview { view 2 }
                set { name "FOV" value 52 }
                singleview {}
            }
            """);
        string scriptPath = Path.ChangeExtension(source, ".lua");
        var converted = TourConverter.ConvertFile(source);
        Require(converted.Unapplied.Count == 0, "Every classic view command must convert.");
        File.WriteAllText(scriptPath, converted.Lua);
        try
        {
            var running = _scene.RunScript(scriptPath) ?? throw new InvalidOperationException("Open the classic view script.");
            running.Step(0);
            Require(Session.ViewCount == 3 && Session[2] == right && running.View == 1 &&
                Session.Layout.First is { Direction: ViewSplit.Stacked, First.View: 2, Second.View: 0 } branch &&
                Math.Abs(branch.FirstShare - .65) < 1e-12, "Split the named leaf below its clone at the authored share.");
            running.Step(1);
            Require(running.View == 2 && Session[2].FieldOfViewDegrees == 36 && Session[0] == left && Session[1] == right &&
                _scene.Renderer(2).FindChild("ScriptImage", true, false) is TextureRect &&
                _scene.Renderer(1).FindChild("ScriptImage", true, false) is null, "Switch the script and overlay to the newly active view.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var view = _scene.Renderer(2).GetViewport().GetTexture().GetImage();
                Require(!view.IsEmpty(), "Render the selected split on the GPU.");
            }
            running.Step(1);
            Require(Session.ViewCount == 2 && running.View == 1 && Session[1].Selection == Jason &&
                Session[1].Epoch == right.Epoch, "Deleting a view must activate the first sibling leaf.");
            running.Step(1);
            Require(Session.ViewCount == 1 && running.View == 0 && Session[0].FieldOfViewDegrees == 36,
                "Removing a neighboring leaf must preserve the active observer.");
            running.Step(1);
            Require(running.Finished && running.Error is null && Session.ViewCount == 1 &&
                running.View == 0 && Session[0].FieldOfViewDegrees == 52 && Session[0].Selection == Jason,
                "Single view must keep the observer selected by the script.");
            GD.Print("Classic view script: split, selected, displayed local media and removed views without losing the Jason-1 observer.");
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "Restore the original workspace after classic views.");
        }
    }

    private async Task ExerciseOriginalTours(string directory, string sources)
    {
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        string restore = Path.Combine(directory, "before-tours.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        var scripts = Find<Button>("ScriptsTab");
        if (!scripts.ButtonPressed)
            await Click(scripts);
        var menu = Find<MenuButton>("ScriptMenu");
        var source = _scene.GetNode<FileDialog>("ConvertTourSource");
        var saveDialog = _scene.GetNode<FileDialog>("ConvertedTourTarget");
        source.CurrentDir = sources;
        saveDialog.CurrentDir = directory;
        await Click(menu);
        await ChooseMenuItem(menu.GetPopup(), 2);
        await Until(() => source.Visible, "Opening the original tour file chooser");
        await Click(source.GetLineEdit());
        await KeyPress(Key.A, ctrl: true);
        await TypeText("demo.cel");
        await KeyPress(Key.Enter);
        await Until(() => saveDialog.Visible, "Opening the converted Lua save dialog");
        await Click(saveDialog.GetOkButton());
        string demo = Path.Combine(directory, "demo.lua");
        await Until(() => File.Exists(demo), "Saving the converted original demo");
        TourConversion demoConversion = TourConverter.ConvertFile(Path.Combine(sources, "demo.cel"));
        Require(File.ReadAllText(demo) == demoConversion.Lua && demoConversion.Commands >= 60 &&
            !demoConversion.Unapplied.Contains("gotoloc") && !demoConversion.Unapplied.Contains("rotate") &&
            !demoConversion.Unapplied.Contains("changedistance"),
            "The converted original demo must contain runnable Lua and visible notes for unsupported commands.");

        string tour = Path.Combine(directory, "tour-system.lua");
        string originalSystem = Path.Combine(sources, "tour-system.celx");
        TourConversion system = TourConverter.ConvertFile(originalSystem);
        Require(system.Lua == File.ReadAllText(originalSystem), "The original recursive tour must run with its own Lua source.");
        File.WriteAllText(tour, system.Lua);
        var (demoTargets, advanced) = RunToEnd(demo);
        Require(demoTargets.Contains(SolarSystemSample.MoonId) && demoTargets.Contains(PlanetCatalogue.SaturnId) &&
            demoTargets.Contains(SolarSystemSample.SunId) && advanced && Session[0].Navigation?.Target == SolarSystemSample.EarthId,
            "The original demo must visit the Moon, Sun and Saturn, advance time and end at Earth.");
        string localized = Path.Combine(directory, "demo-localized.lua");
        var russian = TourConverter.ConvertFile(Path.Combine(sources, "demo_ru.cel"));
        File.WriteAllText(localized, russian.Lua);
        Require(russian.Commands == demoConversion.Commands && !russian.Unapplied.Contains("synchronous"),
            "The localized demo must contain the same flight and body-lock commands.");
        var opening = _scene.RunScript(localized) ?? throw new InvalidOperationException("Could not open the localized demo.");
        opening.Step(0);
        Require(Session.Notice(0) is { HorizontalOrigin: 0, VerticalOrigin: 0 } caption &&
            caption.Text.StartsWith("Знакомство", StringComparison.Ordinal) &&
            (Session.ViewCount == 1 || Session.Notice(1) is null), "The original opening must appear only in its own observer.");
        Session.Tick(6);
        Require(Session.Notice(0) is null, "A paused observer must still expire its caption.");
        var (localizedTargets, _) = RunToEnd(localized);
        Require(localizedTargets.Contains(SolarSystemSample.MoonId) && localizedTargets.Contains(PlanetCatalogue.SaturnId),
            "The localized demo must visit the original Moon and Saturn.");
        var (tourTargets, _) = RunToEnd(tour);
        Require(tourTargets.Count >= 18 && tourTargets.Contains(new("sol/titan")) && tourTargets.Contains(new("sol/neptune")),
            "The converted recursive tour must visit the loaded Solar System including Titan and Neptune.");

        var guide = TourConverter.ConvertFile(Path.Combine(sources, "guide.cel"));
        Require(guide.Commands == 11 && guide.Unapplied.Count == 0 && guide.Lua.Contains("13463808362", StringComparison.Ordinal),
            "The original guide must retain its eleven stops and convert AU distances to kilometres.");
        string guidePath = Path.Combine(directory, "guide.lua");
        File.WriteAllText(guidePath, guide.Lua);
        var settingsFile = new LocalAddonSettingsFile();
        LocalAddonSettings previousAddOns = settingsFile.Load();
        string? guideBodies = System.Environment.GetEnvironmentVariable("SPACETANGENT_GUIDE_BODIES");
        HashSet<ObjectId> guideTargets;
        try
        {
            if (guideBodies is not null)
            {
                var guideSource = settingsFile.FindOrCreate(previousAddOns, LocalAddonKind.BodyCatalog, guideBodies);
                Require(await _scene.ReloadAddOns(previousAddOns.SetSource(guideSource, true)),
                    "Loading the original asteroid and comet catalogues for the guide.");
            }
            (guideTargets, _) = RunToEnd(guidePath);
            if (guideBodies is not null)
                Require(new[] { "Eros", "Ida", "Borrelly" }.All(name =>
                        guideTargets.Any(id => Session.World.Catalogue.Find(id)?.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase) == true)),
                    "The guide must visit Eros, Ida and Borrelly when their original catalogues are loaded.");
            if (guideBodies is not null && System.Environment.GetEnvironmentVariable("SPACETANGENT_STC_ROOT") is { Length: > 0 })
                Require(guideTargets.Count == 11, "The full original guide must visit all eleven destinations.");
        }
        finally
        {
            if (guideBodies is not null)
            {
                Require(await _scene.ReloadAddOns(previousAddOns), "Restoring the body catalogue after the guide.");
            }
        }
        Require(guideTargets.Contains(new("sol/jupiter")) && guideTargets.Contains(new("sol/pluto")),
            "The original guide must complete and visit Jupiter and Pluto.");
        GD.Print($"UI: converted the original eleven-stop guide and visited {guideTargets.Count} loaded targets.");

        Session.SelectObject(0, PlanetCatalogue.MarsId);
        CheckClassic("goto.cel");
        Require(Session[0].Selection == SolarSystemSample.EarthId, "The original goto script must finish at Earth.");
        CheckClassic("gotoloc.cel");
        CheckClassic("orbit.cel");
        Require(Session[0].Selection == PlanetCatalogue.MarsId, "The original orbit script must retain Mars.");

        var star = Session.World.Stellar.FindByName("Sirius A")?.Id ?? new ObjectId("hip/32349");
        Session.SelectObject(0, star);
        CheckLua("absmag.celx", "abs magnitude = ");
        CheckLua("distanceto.celx", "distance to selection = ");
        CheckLua("annum.celx", null);
        var oneYear = JulianInstant.FromJulianDate(2460634.5 + 365, TimeScale.Tdb);
        Require(Session[0].Epoch == oneYear, "The original annum script must advance exactly 365 TDB days.");
        GD.Print("UI: ran the original absolute magnitude, distance and one-year CELX scripts and three classic flight scripts.");

        void CheckClassic(string name)
        {
            var converted = TourConverter.ConvertFile(Path.Combine(sources, name));
            Require(converted.Unapplied.Count == 0, $"All commands in {name} must be converted.");
            string path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".lua");
            File.WriteAllText(path, converted.Lua);
            RunToEnd(path);
        }

        void CheckLua(string name, string? notice)
        {
            var converted = TourConverter.ConvertFile(Path.Combine(sources, name));
            string path = Path.Combine(directory, Path.GetFileNameWithoutExtension(name) + ".lua");
            File.WriteAllText(path, converted.Lua);
            RunToEnd(path);
            if (notice is not null)
                Require(Session.Notice(0)?.Text.StartsWith(notice, StringComparison.Ordinal) == true,
                    $"The original {name} must display its calculated result.");
        }

        LuaSessionScript stopping = _scene.RunScript(tour) ?? throw new InvalidOperationException("Could not start the tour to test Escape.");
        stopping.Step(.05);
        Require(Session.IsFlying(0), "The running tour must have begun a flight before Escape.");
        await KeyPress(Key.Escape);
        Require(stopping.Finished && !Session.IsFlying(0), "Escape must stop the running tour and its flight.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CONVERTED_TOURS") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.Copy(guidePath, Path.Combine(output, "guide.lua"), overwrite: true);
            File.Copy(demo, Path.Combine(output, "demo.lua"), overwrite: true);
            File.Copy(tour, Path.Combine(output, "tour-system.lua"), overwrite: true);
            File.WriteAllLines(Path.Combine(output, "conversion-notes.txt"),
                new[] { $"demo.cel: {demoConversion.Commands} converted command blocks." }.Concat(demoConversion.Unapplied)
                .Concat(new[] { "", "tour-system.celx: verified recursive tour." }).Concat(system.Unapplied));
        }
        await ExerciseScriptMedia(directory);
        Require(_scene.LoadScenario(restore), "The scene before the tour must be restorable.");
        await ExerciseClassicViews(directory);
        GD.Print($"UI: converted original demo ({demoConversion.Commands} command blocks) and recursive tour ({tourTargets.Count} targets), completed both, and stopped a rerun with Escape");

        (HashSet<ObjectId>, bool) RunToEnd(string path)
        {
            _scene.Seek(0, JulianInstant.FromJulianDate(2460634.5, TimeScale.Tdb));
            Session.SetPlaying(0, false);
            JulianInstant start = Session[0].Epoch;
            LuaSessionScript script = _scene.RunScript(path) ?? throw new InvalidOperationException("Could not start the converted tour.");
            Require(script.View == 0, "The converted tour must run in the active left view.");
            const double frame = .125;
            var arrived = new HashSet<ObjectId>();
            bool advanced = false;
            for (int iteration = 0; !script.Finished && iteration < 1400; iteration++)
            {
                Session.Tick(frame);
                script.Step(frame);
                Session.UpdateCamera(0, Session.World.Graph.Evaluate(Session[0].Epoch), frame);
                if (!Session.IsFlying(0) && Session[0].Navigation?.Target is { } target)
                    arrived.Add(target);
                advanced |= Session[0].Epoch.CompareTo(start) > 0;
            }
            Require(script.Finished && script.Error is null, $"The converted tour must finish within the quotas ({script.Error}).");
            return (arrived, advanced);
        }
    }
}
