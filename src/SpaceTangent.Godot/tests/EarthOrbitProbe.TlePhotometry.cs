// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseDenseTle(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var group = session.World.LocalSatellites.Where(satellite =>
            satellite.Name.StartsWith("STARLINK-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (group.Length < 10_000)
            throw new InvalidOperationException("The archived dense Starlink catalogue is required.");

        var epoch = group[0].Epoch;
        var first = Observer(epoch, false);
        var second = Observer(epoch.AddSeconds(600), true);
        var scenario = new SimulationScenario([first, second], false, 0, 0, session.DeltaT);
        string path = Path.Combine(directory, "dense-tle.stscene");
        ScenarioFile.Save(path, scenario);
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the dense satellite scene.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        scene.Seek(0, first.Epoch);
        scene.Seek(1, second.Epoch);

        CheckPoints();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        const int samples = 60;
        for (int sample = 1; sample <= samples; sample++)
            scene.Seek(sample % 2, epoch.AddSeconds(5 * sample + (sample % 2) * 600));
        timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var expected = session.CaptureScenario(1);
        int visible = CheckPoints();
        if (!await scene.ReloadAddOns() ||
            !session.CaptureScenario(1).Views.SequenceEqual(expected.Views) || session.DeltaT != expected.DeltaT)
            throw new InvalidOperationException("Dense satellite reload lost the saved cameras or time settings.");
        int afterReload = CheckPoints();
        if (visible != afterReload)
            throw new InvalidOperationException("Dense satellite reload changed the visible point count.");

        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the dense satellite scene.");
        GD.Print(FormattableString.Invariant(
            $"Dense TLE GPU: {group.Length} archived satellites, {visible}/{afterReload} visible before/after reload, {samples} two-view updates at {timer.Elapsed.TotalMilliseconds / samples:F1} ms and {allocated / samples:N0} managed bytes each."));

        string profile = Path.Combine(directory, "archived-starlink-settings.json");
        File.WriteAllText(profile, """{"Site":{},"Satellites":false,"Starlink":true}""");
        var imported = LegacyObserverProfiles.Read(profile, first, session.DeltaT).Observer;
        var enabled = first with { Sky = imported.Sky };
        var disabled = first with { Sky = first.Sky with { Starlink = false } };
        string settings = Path.Combine(directory, "starlink-independent.stscene");
        ScenarioFile.Save(settings, scenario with { Views = [enabled, disabled] });
        if (!scene.LoadScenario(settings) || !await scene.ReloadAddOns() ||
            session[0].Sky.Starlink != true || session[1].Sky.Starlink != false)
            throw new InvalidOperationException("The independent Starlink flags did not survive reload.");
        int archivedVisible = CheckPoints();
        var shown = group.First(satellite => scene.Renderer(0).BodyPoint(satellite.Id)?.Visible == true);
        var toggle = (CheckButton)scene.FindChild("StarlinkLayer", recursive: true, owned: false);
        toggle.ButtonPressed = false;
        if (session[0].Sky.Starlink != false || session[1].Sky.Starlink != false ||
            group.Any(satellite => scene.Renderer(0).BodyPoint(satellite.Id)?.Visible == true))
            throw new InvalidOperationException("Disabling the active Starlink layer left visible unselected points.");
        session.SelectObject(0, shown.Id);
        scene.Seek(0, first.Epoch);
        if (scene.Renderer(0).BodyPoint(shown.Id)?.Visible != true)
            throw new InvalidOperationException("An explicitly selected Starlink satellite must remain visible.");
        GD.Print($"Starlink settings GPU: {archivedVisible} visible with Satellites disabled, independent flags after reload and selected satellite preserved.");

        await ExerciseGroundStarlink(scene, directory, epoch);

        int CheckPoints()
        {
            int shown = group.Count(satellite => scene.Renderer(0).BodyPoint(satellite.Id)?.Visible == true);
            if (shown < 20 || group.Any(satellite => scene.Renderer(1).BodyPoint(satellite.Id)?.Visible == true))
                throw new InvalidOperationException($"Expected the dense satellite field in only one view, found {shown} points.");
            return shown;
        }

        ObserverView Observer(JulianInstant instant, bool hidden)
        {
            var snapshot = session.World.Graph.Evaluate(instant);
            if (snapshot.SolarSystem is not { } solar)
                throw new InvalidOperationException("The dense satellite scene needs the original solar ephemeris.");
            var sun = (solar.Sun - solar.Earth).PositionKilometers;
            var eye = sun / sun.Length;
            var up = Vector3d.Cross(Vector3d.Cross(eye, new(0, 0, 1)), eye);
            const double range = 9000;
            return new(instant, CameraMode.Solar, null, false)
            {
                Navigation = new(solar.Earth.PositionKilometers, range, 0, 0, null, false)
                {
                    Placement = new(eye * range, eye * -1, up / up.Length)
                },
                Sky = new SkyLayerSettings(false, false, false, false) { HideSatellites = hidden }
            };
        }
    }

    private async Task ExerciseGroundStarlink(SolarSystemScene scene, string directory, JulianInstant epoch)
    {
        var session = scene.Session!;
        var site = session.World.EarthPlaces.Places.Single(place => place.EnglishName == "Paranal Observatory").ObserverSite;
        var sky = new SkyLayerSettings(false, false, false, false)
        {
            Starlink = true, HideSatellites = true, HideClouds = true
        };
        string path = Path.Combine(directory, "paranal-starlink.stscene");
        // Near evening or morning twilight satellites can still be sunlit above the dark site.
        for (int step = 0; step < 48; step++)
        {
            var instant = epoch.AddSeconds(step * 1800);
            var solar = session.World.Graph.Evaluate(instant).SolarSystem!.Value;
            var lighting = EarthSurfaceLighting.Sample(solar, site, session.DeltaT);
            if (lighting.SolarAltitudeRadians is < -.38 or > -.19)
                continue;
            var azimuth = EarthObserving.Horizontal(lighting, lighting.EarthToSun)?.AzimuthDegrees ?? 0;
            var enabled = new ObserverView(instant, CameraMode.Surface, null, false)
            {
                EarthObserver = new(site, azimuth, 40) { Conditions = new(1, Refraction: false) },
                Sky = sky,
                FieldOfViewDegrees = 75
            };
            var disabled = enabled with { Sky = sky with { Starlink = false } };
            ScenarioFile.Save(path, new([enabled, disabled], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path))
                throw new InvalidOperationException("Could not open the ground Starlink scene.");
            await Frame();
            int before = scene.Renderer(0).GroundStarlinkPoints;
            if (before < 3)
                continue;
            if (scene.Renderer(1).GroundStarlinkPoints != 0 || !await scene.ReloadAddOns())
                throw new InvalidOperationException("Starlink leaked into the other ground view or add-on reload failed.");
            await Frame();
            if (scene.Renderer(0).GroundStarlinkPoints != before || scene.Renderer(1).GroundStarlinkPoints != 0 ||
                session[0].Sky != enabled.Sky || session[1].Sky != disabled.Sky)
                throw new InvalidOperationException("The ground Starlink settings or visible count changed on reload.");
            using var lit = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            var toggle = (CheckButton)scene.FindChild("StarlinkLayer", recursive: true, owned: false);
            toggle.ButtonPressed = false;
            await Frame();
            using var dark = scene.Renderer(0).GetViewport().GetTexture().GetImage();
            int brighter = 0;
            for (int y = 0; y < lit.GetHeight(); y++)
                for (int x = 0; x < lit.GetWidth(); x++)
                    if (lit.GetPixel(x, y).R > dark.GetPixel(x, y).R + .02f)
                        brighter++;
            if (brighter < before || scene.Renderer(0).GroundStarlinkPoints != 0)
                throw new InvalidOperationException($"The ground shader did not render the Starlink points ({brighter} changed pixels).");
            if (lit.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
                throw new IOException("Could not capture the original Starlink catalogue at Paranal.");

            var sun = lighting.EarthToSun / lighting.EarthToSun.Length;
            if (StarlinkPhotometry.Magnitude(sun * -7000, lighting.PositionKilometers, sun, 5.5) is not null ||
                StarlinkPhotometry.Magnitude(sun * 7000, sun * 8000, sun, 5.5) is not { } full ||
                Math.Abs(full - (5.5 - 2.5 * Math.Log10(2))) > 1e-8)
                throw new InvalidOperationException("Starlink brightness must follow phase and Earth shadow.");
            GD.Print($"Ground Starlink GPU: Paranal, {before} visible satellites, {brighter} brighter pixels, independent reload and Earth shadow.");
            return;
        }
        throw new InvalidOperationException("The archived catalogue did not yield a twilight Starlink field at Paranal.");

        async Task Frame()
        {
            for (int index = 0; index < 3; index++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }
    }

    private async Task ExerciseTlePhotometry(SolarSystemScene scene, string directory)
    {
        var world = scene.Session!.World;
        var iss = world.FindLocalSatellite(new("norad/25544")) ??
            throw new InvalidOperationException("The original ISS TLE is required.");
        var fregat = world.FindLocalSatellite(new("norad/49271")) ??
            throw new InvalidOperationException("The original FREGAT TLE is required.");
        await CheckSatellitePhotometry(scene, directory, iss.Id, iss.Epoch, "iss-photometry", .005, .3, -2.5);
        await CheckSatellitePhotometry(scene, directory, fregat.Id, fregat.Epoch, "fregat-photometry", .005, .3);
        var jason = world.Satellites.Single(track => track.Id == new ObjectId("norad/26997"));
        await CheckSatellitePhotometry(scene, directory, jason.Id, jason.Epoch, "jason-photometry", .01, .5);
        GD.Print("Science photometry GPU: original Jason-1 catalogue radius, selectable point and independent eclipse.");
        GD.Print("TLE photometry GPU: original ISS -2.5 at 1000 km half lit, selectable point and independent eclipse.");
        GD.Print("TLE photometry GPU: original FREGAT debris uses archived default radius and albedo, with Earth shadow.");
    }

    private async Task CheckSatellitePhotometry(SolarSystemScene scene, string directory, ObjectId id, JulianInstant epoch,
        string sceneName, double legacyRadius, double legacyAlbedo, double? standardMagnitude = null)
    {
        var session = scene.Session!;
        const double range = 1000;
        ObserverView? day = null, night = null;
        double diffuseMagnitude = double.NaN;
        for (int minute = 0; minute <= 120 && (day is null || night is null); minute++)
        {
            JulianInstant instant = epoch.AddSeconds(minute * 60);
            var snapshot = session.World.Graph.Evaluate(instant);
            if (!snapshot.TryState(id, out var station) || snapshot.SolarSystem is not { } solar)
                continue;
            Vector3d fromEarth = (station - solar.Earth).PositionKilometers;
            Vector3d toSun = (solar.Sun - station).PositionKilometers;
            double sunDistance = toSun.Length;
            toSun /= sunDistance;
            double along = Vector3d.Dot(fromEarth, toSun);
            bool shadow = along < 0 && (fromEarth - toSun * along).Length < EarthSurfaceLighting.EarthRadiusKilometers - 50;
            var tangent = Vector3d.Cross(toSun, fromEarth);
            if (tangent.Length <= 0)
                continue;
            Vector3d direction = tangent / tangent.Length;
            Vector3d up = toSun;
            var pose = new NavigationCamera(station.PositionKilometers, range, 0, 0, id, true)
            {
                Placement = new NavigationPlacement(direction * range, direction * -1, up)
            };
            var view = new ObserverView(instant, CameraMode.Solar, id, false) { Navigation = pose };
            if (shadow)
                night ??= view;
            else if (along > 0 && day is null)
            {
                day = view;
                double ratio = legacyRadius / range * (149_597_870.7 / sunDistance);
                diffuseMagnitude = -26.74 - 2.5 * Math.Log10(legacyAlbedo * .5 * ratio * ratio);
            }
        }
        if (day is not { } illuminated || night is not { } eclipsed)
            throw new InvalidOperationException("The satellite orbit must provide both sunlit and eclipsed epochs.");
        string path = Path.Combine(directory, sceneName + ".stscene");
        var labels = new SkyLayerSettings(false, true, false, false);
        ScenarioFile.Save(path, new([illuminated with { Sky = labels }, eclipsed], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("Could not open the two satellite observers.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        scene.Seek(0, illuminated.Epoch);
        scene.Seek(1, eclipsed.Epoch);
        var renderer = scene.Renderer(0);
        if (renderer.BodyPoint(id) is not { Visible: true, MaterialOverride: ShaderMaterial material } point ||
            Math.Abs(material.GetShaderParameter("magnitude").AsSingle() - (standardMagnitude ?? diffuseMagnitude)) > 1e-4 ||
            scene.Renderer(1).BodyPoint(id)?.Visible == true || scene.Renderer(1).LocalSatelliteVisible(id) ||
            scene.Renderer(1).ScienceBodyNode(id)?.Visible == true)
            throw new InvalidOperationException("The satellite must match its archived brightness at 1000 km and disappear in Earth's shadow.");
        if (renderer.BodyLabel(id) is not { Visible: true } || scene.Renderer(1).BodyLabel(id)?.Visible == true)
            throw new InvalidOperationException("The selected satellite label must appear only in the enabled view.");
        if (renderer.PickObject(renderer.NavigationCameraNode.UnprojectPosition(point.Position)) != id)
            throw new InvalidOperationException("The sunlit satellite point must remain selectable.");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the two original satellite states.");

        if (standardMagnitude.HasValue)
        {
            var free = illuminated with
            {
                Selection = null,
                Navigation = illuminated.Navigation!.Value with { Target = null, Tracking = false }
            };
            var hidden = free with { Sky = free.Sky with { HideSatellites = true } };
            string layerPath = Path.Combine(directory, "iss-satellite-layer.stscene");
            ScenarioFile.Save(layerPath, new([free, hidden], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(layerPath) || !await scene.ReloadAddOns() ||
                session[0].Sky.HideSatellites || !session[1].Sky.HideSatellites ||
                scene.Renderer(0).BodyPoint(id) is not { Visible: true } freePoint ||
                scene.Renderer(0).PickObject(scene.Renderer(0).NavigationCameraNode.UnprojectPosition(freePoint.Position)) != id ||
                scene.Renderer(1).BodyPoint(id)?.Visible == true)
                throw new InvalidOperationException("The ISS point must be selectable with a free camera and hidden only in the disabled view after reload.");
            GD.Print("TLE layer GPU: original ISS is selectable without tracking and hidden in the independent saved view.");
        }

        if (renderer.ScienceBodyNode(id) is null)
            return;
        var farPose = illuminated.Navigation!.Value;
        var farPlacement = farPose.Placement!.Value;
        const double nearRange = .08;
        var nearPose = farPose with
        {
            RangeKilometers = nearRange,
            Placement = farPlacement with { OffsetKilometers = farPlacement.OffsetKilometers * (nearRange / range) }
        };
        string nearPath = Path.Combine(directory, sceneName + "-near.stscene");
        ScenarioFile.Save(nearPath, new([illuminated with { Sky = labels }, illuminated with { Navigation = nearPose }],
            false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(nearPath) || !await scene.ReloadAddOns() ||
            scene.Renderer(0).BodyPoint(id)?.Visible != true || scene.Renderer(0).ScienceBodyNode(id)?.Visible == true ||
            scene.Renderer(1).ScienceBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } || mesh.GetSurfaceCount() != 28 ||
            scene.Renderer(1).BodyPoint(id)?.Visible == true)
            throw new InvalidOperationException("Reload must preserve the Jason-1 point and its original textured model in separate views.");
    }
}
