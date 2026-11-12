// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
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
            demoConversion.Unapplied.Contains("changedistance"),
            "The converted original demo must contain runnable Lua and visible notes for unsupported commands.");

        string tour = Path.Combine(directory, "tour-system.lua");
        TourConversion system = TourConverter.ConvertFile(Path.Combine(sources, "tour-system.celx"));
        File.WriteAllText(tour, system.Lua);
        var (demoTargets, advanced) = RunToEnd(demo);
        Require(demoTargets.Contains(SolarSystemSample.MoonId) && demoTargets.Contains(PlanetCatalogue.SaturnId) &&
            demoTargets.Contains(SolarSystemSample.SunId) && advanced && Session[0].Navigation?.Target == SolarSystemSample.EarthId,
            "The original demo must visit the Moon, Sun and Saturn, advance time and end at Earth.");
        var (tourTargets, _) = RunToEnd(tour);
        Require(tourTargets.Count >= 18 && tourTargets.Contains(new("sol/titan")) && tourTargets.Contains(new("sol/neptune")),
            "The converted recursive tour must visit the loaded Solar System including Titan and Neptune.");

        LuaSessionScript stopping = _scene.RunScript(tour) ?? throw new InvalidOperationException("Could not start the tour to test Escape.");
        stopping.Step(.05);
        Require(Session.IsFlying(0), "The running tour must have begun a flight before Escape.");
        await KeyPress(Key.Escape);
        Require(stopping.Finished && !Session.IsFlying(0), "Escape must stop the running tour and its flight.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_CONVERTED_TOURS") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            File.Copy(demo, Path.Combine(output, "demo.lua"), overwrite: true);
            File.Copy(tour, Path.Combine(output, "tour-system.lua"), overwrite: true);
            File.WriteAllLines(Path.Combine(output, "conversion-notes.txt"),
                new[] { $"demo.cel: {demoConversion.Commands} converted command blocks." }.Concat(demoConversion.Unapplied)
                .Concat(new[] { "", "tour-system.celx: verified recursive tour." }).Concat(system.Unapplied));
        }
        Require(_scene.LoadScenario(restore), "The scene before the tour must be restorable.");
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
