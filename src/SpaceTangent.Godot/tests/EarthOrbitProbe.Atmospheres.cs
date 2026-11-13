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

        if (native)
            GD.Print("Atmosphere lookup active: Earth.");
        var rows = new List<(string Name, ObjectId Id, double Radius, double InsideHeight, bool Lookup)>
        {
            ("venus", SolarSystemSample.VenusId, 6051.8, 85, false)
        };
        if (native)
        {
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ATMOSPHERE_PROBE") != "earth-titan")
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
            if (id == SolarSystemSample.VenusId && scene.Renderer(0).VenusCloudState is { } first)
            {
                if (!first.Visible)
                    throw new InvalidOperationException("The original Venus clouds must be visible at 85 km.");
                var session = scene.Session!;
                var clear = session[0];
                string profilePath = Path.Combine(directory, "archived-cloud-settings.json");
                File.WriteAllText(profilePath, """{"Site":{},"Clouds":false}""");
                var imported = LegacyObserverProfiles.Read(profilePath, clear, session.DeltaT).Observer;
                string viewsPath = Path.Combine(directory, "venus-independent-clouds.stscene");
                ScenarioFile.Save(viewsPath, new([imported, clear], false, 0, 0, session.DeltaT));
                if (!scene.LoadScenario(viewsPath) || !await scene.ReloadAddOns() ||
                    scene.Session![0] != imported || scene.Session[1] != clear ||
                    scene.Renderer(0).VenusCloudState is not { Visible: false } ||
                    scene.Renderer(1).VenusCloudState is not { Visible: true } ||
                    !scene.Renderer(0).WorldAtmosphereActive(id, lookup: false))
                    throw new InvalidOperationException("The archived Clouds preference must hide only the cloud deck in its observer.");
                GD.Print("Cloud settings GPU: original Venus cloud deck independent of atmosphere and second view after reload.");
                File.WriteAllText(profilePath, """{"Site":{},"Atmospheres":false}""");
                var noAir = LegacyObserverProfiles.Read(profilePath, clear, session.DeltaT).Observer;
                string airPath = Path.Combine(directory, "venus-independent-atmospheres.stscene");
                ScenarioFile.Save(airPath, new([noAir, clear], false, 0, 0, session.DeltaT));
                if (!scene.LoadScenario(airPath) || !await scene.ReloadAddOns() ||
                    scene.Session![0] != noAir || scene.Session[1] != clear ||
                    scene.Renderer(0).WorldAtmosphereActive(id, lookup: false) ||
                    !scene.Renderer(1).WorldAtmosphereActive(id, lookup: false) ||
                    scene.Renderer(0).VenusCloudState is not { Visible: true } ||
                    scene.Renderer(1).VenusCloudState is not { Visible: true })
                    throw new InvalidOperationException("The archived Atmospheres preference must hide only one atmosphere and preserve both cloud decks.");
                if (DisplayServer.GetName() != "headless")
                {
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var withoutAir = scene.Renderer(0).GetViewport().GetTexture().GetImage();
                    using var withAir = scene.Renderer(1).GetViewport().GetTexture().GetImage();
                    var center0 = withoutAir.GetPixel(withoutAir.GetWidth() / 2, 3 * withoutAir.GetHeight() / 4);
                    var center1 = withAir.GetPixel(withAir.GetWidth() / 2, 3 * withAir.GetHeight() / 4);
                    if (Math.Abs(center0.R - center1.R) + Math.Abs(center0.G - center1.G) + Math.Abs(center0.B - center1.B) < .02)
                        throw new InvalidOperationException("The saved atmosphere preference must change the rendered Venus cloud color.");
                    using var comparison = GetViewport().GetTexture().GetImage();
                    if (comparison.SavePng(Path.ChangeExtension(airPath, ".png")) != Error.Ok)
                        throw new IOException("Could not save the Venus atmosphere comparison.");
                }
                GD.Print("Atmosphere settings GPU: original Venus atmosphere hidden in one restored view with both cloud decks visible.");
                var later = epoch.AddSeconds(86400);
                if (!scene.Session.World.Graph.Evaluate(later).TryState(id, out var laterState))
                    throw new InvalidOperationException("Venus must have a state one day later.");
                await Capture("cloud-drift", laterState.PositionKilometers + outward * (radius * 3.5), outward * -1, up, later);
                var drift = scene.Renderer(0).VenusCloudState;
                if (drift is not { Visible: true } || Math.Abs(Math.Abs(drift.Value.Offset - first.Offset) % 1 - .25) > 1e-6 &&
                    Math.Abs(Math.Abs(drift.Value.Offset - first.Offset) % 1 - .75) > 1e-6)
                    throw new InvalidOperationException("Venus clouds must drift by 90 degrees per TDB day.");
                await Capture("below-clouds", state.PositionKilometers + outward * (radius + 55), along, outward);
                if (scene.Renderer(0).VenusCloudState is not { Visible: false })
                    throw new InvalidOperationException("The opaque Venus cloud layer must be hidden below 65 km.");
                GD.Print("Venus clouds: original texture at 65 km, 90 degrees per TDB day, visible above and hidden below.");
            }

            async Task Capture(string place, Vector3d eye, Vector3d direction, Vector3d vertical, JulianInstant? instant = null)
            {
                string basename = $"atmosphere-{name}-{place}";
                var camera = new NavigationCamera(eye, radius * 2.5, 0, 0, null, false)
                    { Placement = new(default, direction, vertical) };
                var view = new ObserverView(instant ?? epoch, CameraMode.Solar, id, false) { Navigation = camera };
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
