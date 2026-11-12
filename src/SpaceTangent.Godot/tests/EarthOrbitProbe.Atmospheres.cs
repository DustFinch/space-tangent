// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExercisePlanetAtmospheres(SolarSystemScene scene, JulianInstant epoch, string directory)
    {
        bool native = !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_ROOT"));
        if (native && !scene.Renderer(0).WorldAtmosphereActive(SolarSystemSample.EarthId, lookup: true))
            throw new InvalidOperationException("The Earth orbit probe did not use the original lookup tables.");

        var rows = new List<(string Name, ObjectId Id, double Radius, double InsideHeight, bool Lookup)>
        {
            ("venus", SolarSystemSample.VenusId, 6051.8, 85, false)
        };
        if (native)
        {
            rows.Add(("mars", PlanetCatalogue.MarsId, 3396.19, 15, true));
            rows.Add(("titan", new("sol/titan"), 2574.73, 30, true));
        }
        var snapshot = scene.Session!.World.Graph.Evaluate(epoch);
        foreach (var (name, id, radius, insideHeight, lookup) in rows)
        {
            if (!snapshot.TryState(id, out CartesianState state))
                throw new InvalidOperationException($"{name} does not have an ephemeris for the atmosphere check.");
            var light = (snapshot.SolarSystem!.Value.Sun - state).PositionKilometers;
            light /= light.Length;
            var tangent = Vector3d.Cross(new(0, 0, 1), light);
            tangent /= tangent.Length;
            var outward = light + tangent * .35;
            outward /= outward.Length;
            var up = Vector3d.Cross(outward, Vector3d.Cross(new(0, 0, 1), outward));
            up /= up.Length;
            await Capture("outside", state.PositionKilometers + outward * (radius * 3.5), outward * -1, up);
            var along = tangent - outward * Vector3d.Dot(tangent, outward);
            along /= along.Length;
            await Capture("inside", state.PositionKilometers + outward * (radius + insideHeight), along, outward);

            async Task Capture(string place, Vector3d eye, Vector3d direction, Vector3d vertical)
            {
                string basename = $"atmosphere-{name}-{place}";
                var camera = new NavigationCamera(eye, radius * 2.5, 0, 0, null, false)
                    { Placement = new(default, direction, vertical) };
                var view = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = camera };
                string path = Path.Combine(directory, basename + ".stscene");
                ScenarioFile.Save(path, new([view], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
                if (!scene.LoadScenario(path) || !scene.Renderer(0).WorldAtmosphereActive(id, lookup))
                    throw new InvalidOperationException($"The {name} atmosphere was not active in the {place} camera.");
                if (DisplayServer.GetName() != "headless")
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var image = GetViewport().GetTexture().GetImage();
                    if (image.SavePng(Path.Combine(directory, basename + ".png")) != Error.Ok)
                        throw new IOException($"Could not capture {basename}.");
                }
                GD.Print($"Atmosphere camera restored: {name} {place}, native tables {lookup}.");
            }
        }
    }
}
