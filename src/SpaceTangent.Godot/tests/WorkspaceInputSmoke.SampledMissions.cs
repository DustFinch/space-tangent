// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseSampledMissions(string directory, string source)
    {
        string local = Path.Combine(directory, "Original mission catalogues");
        CopyDirectory(source, local);
        const string variable = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? priorRoot = System.Environment.GetEnvironmentVariable(variable);
        System.Environment.SetEnvironmentVariable(variable, local);
        string restore = Path.Combine(directory, "before-missions.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        var msl = new ObjectId("mission/msl");
        var dawn = new ObjectId("mission/dawn");
        try
        {
            await Reload("Loading original mission catalogues");
            await Fly("MSL", msl, 2456145.5);
            Require(Current().TryStateCenter(msl, out var center) && center == SolarSystemSample.SunId,
                "The MSL cruise arc must be heliocentric.");
            _scene.Seek(0, JulianInstant.FromJulianDate(2456145.75, TimeScale.Tdb));
            var landed = Current();
            Require(landed.TryStateCenter(msl, out center) && center == PlanetCatalogue.MarsId,
                "The landed phase must use Mars as its center.");
            Require(landed.TryState(msl, out var probe), "The landed MSL state must be available.");
            Require(landed.TryState(PlanetCatalogue.MarsId, out var mars), "The Mars reference state must be available.");
            Require(Math.Abs((probe - mars).PositionKilometers.Length - 3389.7007554998277) < .0001,
                "The body-fixed point must retain its original radius.");
            Require(Session[0].Navigation is { Tracking: true, Target: { } target } && target == msl,
                "Tracking must follow the same mission across the center change.");

            await Fly("Dawn", dawn, 2455800);
            Require(Current().TryStateCenter(dawn, out center) && center == new ObjectId("sol/vesta"),
                "The original Dawn orbit must use Vesta as its center.");
            var accepted = Session.CaptureScenario(0);
            var world = Session.World;
            string track = Directory.EnumerateFiles(local, "dawn-vesta-orbit.xyzv", SearchOption.AllDirectories).Single();
            File.Move(track, track + ".missing");
            try
            {
                await OpenAddOns();
                await Click(Find<Button>("ReloadAddOns"));
                await Until(() => _scene.LastAddonDiagnostic is not null, "Reporting the missing Dawn trajectory", 20);
                Require(_scene.LastAddonDiagnostic is { Code: AddonIssue.MissingResource, File: { } missing } &&
                    Path.GetFileName(missing) == Path.GetFileName(track) && ReferenceEquals(world, Session.World),
                    "A missing mission track must retain the last published world.");
                RequireScenario(accepted, Session.CaptureScenario(0), "Rejected Dawn trajectory reload");
            }
            finally
            {
                File.Move(track + ".missing", track);
            }
            await Reload("Restoring the original Dawn trajectory");
            RequireScenario(accepted, Session.CaptureScenario(0), "Reloading the original Dawn trajectory");
            _scene.Seek(0, JulianInstant.FromJulianDate(2456300, TimeScale.Tdb));
            Require(Current().TryStateCenter(dawn, out center) && center == SolarSystemSample.SunId,
                "The second cruise must return Dawn to the Sun's frame.");
            GD.Print("UI: flew to original MSL and Dawn, tracked phase changes, and preserved the camera through failed and successful reloads");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, priorRoot);
        }
        await Reload("Restoring the supplied mission root");
        Require(_scene.LoadScenario(restore), "The previous workspace must reopen after testing missions.");

        SpaceTangent.Core.World.WorldSnapshot Current() => Session.World.Graph.Evaluate(Session[0].Epoch);

        async Task Fly(string name, ObjectId id, double day)
        {
            _scene.Seek(0, JulianInstant.FromJulianDate(day, TimeScale.Tdb));
            await Click(Find<SubViewportContainer>("ViewCanvas0"));
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", name);
            await Click(Result(id));
            await Click(Find<Button>("GoToObject"));
            Require(Session.IsFlying(0), $"Flight to {name} must start.");
            await Until(() => !Session.IsFlying(0), $"Flying to {name}");
            await Click(Find<Button>("TrackObject"));
            Require(_scene.Renderer(0).ImportedBodyNode(id) is { Visible: true },
                $"{name} must have a visible geometry marker.");
        }

        async Task OpenAddOns()
        {
            var panel = Find<Button>("AddOnsTab");
            if (!panel.ButtonPressed)
                await Click(panel);
        }

        async Task Reload(string caption)
        {
            await OpenAddOns();
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns(caption);
        }
    }
}
