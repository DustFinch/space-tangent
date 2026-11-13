// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionViewpoints(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_VIEWPOINT_GPU") != "1" ||
            System.Environment.GetEnvironmentVariable("SPACETANGENT_VIEWPOINT_CORPUS") is not { Length: > 0 } root)
            return;
        const string input = "SPACETANGENT_SAMPLED_MISSIONS_ROOT";
        string? original = System.Environment.GetEnvironmentVariable(input);
        string prior = Path.Combine(directory, "before-mission-viewpoints.stscene");
        if (!scene.SaveScenario(prior))
            throw new IOException("Could not save the observers before installing viewpoints.");
        var session = scene.Session!;
        var menu = (MenuButton?)scene.FindChild("MissionViewpoints", recursive: true, owned: false)
            ?? throw new InvalidOperationException("The bookmarks panel is missing the mission viewpoint menu.");
        var popup = menu.GetPopup();
        var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        var earth = SolarSystemSample.EarthId;
        var first = new ObserverView(epoch, CameraMode.Solar, earth, false);
        var second = new ObserverView(epoch.AddSeconds(25 * 86400), CameraMode.Solar, earth, false);
        var deltaT = session.DeltaT;
        string baseline = Path.Combine(directory, "mission-viewpoint-observers.stscene");
        try
        {
            foreach (string format in new[] { "source", "packed" })
            {
                System.Environment.SetEnvironmentVariable(input, Path.Combine(root, "p4-viewpoint-" + format));
                if (!await scene.ReloadAddOns() || popup.ItemCount != 2 ||
                    popup.GetItemText(0) != "Earth from sunward" || popup.GetItemText(1) != "Earth from above")
                    throw new InvalidOperationException($"The {format} menu did not retain both valid viewpoints.");
                ScenarioFile.Save(baseline, new([first, second], false, 0, 0, deltaT));
                if (!scene.LoadScenario(baseline))
                    throw new InvalidOperationException("Could not restore the initial viewpoint observers.");
                popup.EmitSignal(PopupMenu.SignalName.IdPressed, 0);
                var left = session[0];
                if (session[1] != second)
                    throw new InvalidOperationException("Opening a viewpoint changed the inactive observer.");
                var sun = session.World.Ephemeris.Sample(left.Epoch);
                const double obliquity = 23.4392911 * Math.PI / 180;
                var north = new Vector3d(0, -Math.Sin(obliquity), Math.Cos(obliquity));
                var fromEarth = (sun.Sun - sun.Earth).PositionKilometers;
                Verify(left, first.Epoch, 100_000, fromEarth - north * Vector3d.Dot(fromEarth, north));

                ScenarioFile.Save(baseline, new([left, second], false, 0, 1, deltaT));
                if (!scene.LoadScenario(baseline))
                    throw new InvalidOperationException("Could not activate the second observer.");
                popup.EmitSignal(PopupMenu.SignalName.IdPressed, 1);
                var right = session[1];
                Verify(right, second.Epoch, 50_000, north);
                if (session[0] != left || !await scene.ReloadAddOns() ||
                    session[0] != left || session[1] != right || session.DeltaT != deltaT)
                    throw new InvalidOperationException("The viewpoint cameras or Delta T did not survive reload.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(Path.Combine(directory, $"mission-viewpoints-{format}.png")) != Error.Ok)
                    throw new IOException("Could not capture the viewpoint cameras.");
                GD.Print($"Mission viewpoints GPU ({format}): sunward and polar cameras at 100,000 and 50,000 km altitude; independent epochs and Delta T restored.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(input, original);
        }
        if (!await scene.ReloadAddOns() || !scene.LoadScenario(prior) || popup.ItemCount != 0)
            throw new InvalidOperationException("The removed viewpoints remained in the menu.");

        void Verify(ObserverView view, JulianInstant at, double altitude, Vector3d expected)
        {
            if (view.Epoch != at || view.Selection != earth || view.Navigation is not { Placement: { } pose } camera ||
                Math.Abs(camera.RangeKilometers - session.World.TargetRadiusKilometers(earth) - altitude) > 1e-6 ||
                Vector3d.Dot(pose.OffsetKilometers / pose.OffsetKilometers.Length, expected / expected.Length) < .999999999 ||
                Vector3d.Dot(pose.Forward, pose.OffsetKilometers / pose.OffsetKilometers.Length) > -.999999999)
                throw new InvalidOperationException("The viewpoint did not restore the authored camera pose.");
        }
    }
}
