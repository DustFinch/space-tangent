// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseLocalTle(string directory, string original)
    {
        string local = Path.Combine(directory, "Original TLE groups");
        CopyDirectory(original, local);
        const string variable = "SPACETANGENT_TLE_ROOT";
        string? priorRoot = System.Environment.GetEnvironmentVariable(variable);
        System.Environment.SetEnvironmentVariable(variable, local);
        string restore = Path.Combine(directory, "before-tle.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        var id = new ObjectId("norad/26407");
        try
        {
            await Reload("Loading the archived satellite groups");
            Require(Session.World.LocalSatellites.Count == 243 &&
                Session.World.Catalogue.Search("gps-ops").Count == 32,
                "The four real groups must retain all unique satellites and GPS aliases.");
            var gps = Session.World.FindLocalSatellite(id);
            Require(gps is { DeepSpace: true }, "The original GPS orbit must use the deep-space branch.");
            _scene.Seek(0, gps!.Epoch);
            await Click(Find<SubViewportContainer>("ViewCanvas0"));
            var search = Find<Button>("SearchTab");
            if (!search.ButtonPressed)
                await Click(search);
            await FillField("ObjectQuery", "GPS BIIR-5");
            await Click(Result(id));
            await Click(Find<Button>("GoToObject"));
            Require(Session.IsFlying(0), "The GPS satellite flight must start from the existing search.");
            await Until(() => !Session.IsFlying(0), "Flying to the GPS satellite");
            await Click(Find<Button>("TrackObject"));
            Require(_scene.Renderer(0).LocalSatelliteVisible(id), "The selected GPS marker must be visible.");
            var snapshot = Session.World.Graph.Evaluate(Session[0].Epoch);
            Require(snapshot.TryState(id, out var satellite) && snapshot.TryState(SolarSystemSample.EarthId, out var earth) &&
                (satellite - earth).PositionKilometers.Length is > 26_000 and < 27_000,
                "The GPS state must be placed at its Earth-centered orbit distance.");
            Vector3d eye = Session[0].Navigation!.Value.AnchorKilometers;
            await Click(Find<Button>("AdvanceHour0"));
            Require(Session[0].Navigation!.Value.AnchorKilometers != eye, "The camera must track the changing GPS state.");
            var accepted = Session.CaptureScenario(0);
            var world = Session.World;
            string file = Path.Combine(local, "gps-ops.txt");
            byte[] originalBytes = File.ReadAllBytes(file);
            DateTime writeTime = File.GetLastWriteTimeUtc(file);
            byte[] corrupt = new byte[originalBytes.Length];
            Array.Fill(corrupt, (byte)' ');
            corrupt[0] = (byte)'X';
            File.WriteAllBytes(file, corrupt);
            File.SetLastWriteTimeUtc(file, writeTime);
            try
            {
                await OpenAddOns();
                await Click(Find<Button>("ReloadAddOns"));
                await Until(() => _scene.LastAddonDiagnostic is not null, "Detecting the corrupt TLE replacement", 20);
                Require(ReferenceEquals(world, Session.World), "A corrupt TLE group must retain the published world.");
                RequireScenario(accepted, Session.CaptureScenario(0), "Rejected TLE replacement");
            }
            finally
            {
                File.WriteAllBytes(file, originalBytes);
                File.SetLastWriteTimeUtc(file, writeTime);
            }
            await Reload("Restoring the original GPS group");
            RequireScenario(accepted, Session.CaptureScenario(0), "Restored TLE group");
            _scene.Seek(0, gps.Epoch.AddSeconds(366 * 86400));
            await Until(() => !_scene.Renderer(0).LocalSatelliteVisible(id), "Leaving the GPS propagation window");
            GD.Print("UI: loaded 243 real TLE satellites, searched by group and name, tracked a GPS orbit, and checked content-aware reload and coverage");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, priorRoot);
        }
        await Reload("Restoring the supplied satellite root");
        Require(_scene.LoadScenario(restore), "The earlier workspace must reopen after testing the TLE catalogue.");

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
