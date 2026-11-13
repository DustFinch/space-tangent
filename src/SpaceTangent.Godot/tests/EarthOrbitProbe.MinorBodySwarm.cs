// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMinorBodySwarm(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        string source = LocalDataPaths.Find("SPACETANGENT_MINOR_BODY_CSV", "small-bodies/small-bodies.csv");
        using var input = File.OpenText(source);
        var entries = MinorBodySwarm.Read(input);
        if (entries.Length < 10_000 || entries.Select(entry => entry.Group).Distinct().Count() != 6)
            throw new InvalidOperationException("The original catalogue must contain all six small-body classes.");
        var epoch = JulianInstant.FromJulianDate(2461200.5, TimeScale.Tdb);
        var solar = session.World.Graph.Evaluate(epoch).SolarSystem!.Value;
        var vesta = entries.Single(entry => entry.Name == "Vesta");
        double distance = vesta.Position(epoch).Length;
        if (distance is < 320_000_000 or > 390_000_000 ||
            (vesta.Position(epoch.AddSeconds(86400)) - vesta.Position(epoch)).Length < 1_000_000)
            throw new InvalidOperationException("The archived Vesta orbit did not advance in kilometres.");

        var baseline = new ObserverView(epoch, CameraMode.Solar, null, false)
        {
            Navigation = new(solar.Sun.PositionKilometers, 2.2e9, 0, 0, null, false)
            {
                Placement = new(new(0, 0, 2.2e9), new(0, 0, -1), new(0, 1, 0))
            },
            FieldOfViewDegrees = 75
        };
        string profile = Path.Combine(directory, "archived-small-bodies.json");
        File.WriteAllText(profile, """{"Site":{},"SmallBodies":2}""");
        var imported = LegacyObserverProfiles.Read(profile, baseline, session.DeltaT).Observer;
        var diagram = baseline with { Sky = imported.Sky };
        var named = session.World.Catalogue.Objects.SelectMany(item => item.Aliases.Prepend(item.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nearby = entries.First(entry => entry.Group == 0 && entry.Name.Length > 0 &&
            !named.Contains(entry.Name) && !named.Contains($"{entry.Designation} {entry.Name}") &&
            !named.Contains($"{entry.Designation} ({entry.Name})") && !named.Contains(entry.Designation));
        var place = nearby.Position(epoch);
        var towardSun = place * (-1 / place.Length);
        var up = Vector3d.Cross(towardSun, new(0, 0, 1));
        up /= up.Length;
        var photometry = baseline with
        {
            Navigation = new(solar.Sun.PositionKilometers + place, 100_000, 0, 0, null, false)
            {
                Placement = new(towardSun * 100_000, towardSun * -1, up)
            },
            Sky = baseline.Sky with { SmallBodies = MinorBodyDisplay.Photometric }
        };
        string path = Path.Combine(directory, "minor-body-swarm.stscene");
        ScenarioFile.Save(path, new([diagram, photometry], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns())
            throw new InvalidOperationException("Could not open or reload the original small-body catalogue scene.");
        await Frames();
        int all = scene.Renderer(0).MinorBodyPointCount, bright = scene.Renderer(1).MinorBodyPointCount;
        if (all < 1000 || bright == 0 || bright >= all ||
            session[0].Sky.SmallBodies != MinorBodyDisplay.Diagram || session[1].Sky.SmallBodies != MinorBodyDisplay.Photometric)
            throw new InvalidOperationException($"Incorrect swarm modes after reload ({all} diagram, {bright} photometric).");

        using var before = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        var option = (OptionButton)scene.FindChild("SmallBodySwarmMode", recursive: true, owned: false);
        option.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        await Frames();
        using var after = scene.Renderer(0).GetViewport().GetTexture().GetImage();
        int changed = 0;
        for (int y = 0; y < before.GetHeight(); y++)
            for (int x = 0; x < before.GetWidth(); x++)
                if (before.GetPixel(x, y).R > after.GetPixel(x, y).R + .04f)
                    changed++;
        if (changed < 500 || scene.Renderer(0).MinorBodyPointCount != 0 || scene.Renderer(1).MinorBodyPointCount != bright)
            throw new InvalidOperationException($"The swarm toggle did not remove the expected pixels ({changed}).");
        if (before.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the original small-body swarm.");
        GD.Print($"Minor body swarm GPU: {entries.Length} original entries, {all} diagram points, {bright} photometric points and {changed} changed pixels.");

        async Task Frames()
        {
            for (int index = 0; index < 3; index++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }
    }
}
