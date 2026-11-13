// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseCompactLens(SolarSystemScene scene, string directory)
    {
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_COMPACT_LENS_PROBE") is not ("1" or "only"))
            return;
        var session = scene.Session!;
        ObjectId id = SimulationWorld.CygnusLensId;
        var hole = session.World.BlackHoles.Find(id)!;
        if (!session.World.Catalogue.Search("Cygnus X-1").Any(item => item.Id == id))
            throw new InvalidOperationException("The original name must resolve to the measured compact object.");
        var epoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        var snapshot = session.World.Graph.Evaluate(epoch);
        if (!snapshot.TryState(id, out var compact) || !snapshot.TryState(SimulationWorld.CygnusCompanionId, out var companion))
            throw new InvalidOperationException("The original black hole and its companion must have spatial states.");
        Vector3d relative = (companion - compact).PositionKilometers;
        if (Math.Abs(relative.Length - .2441 * 149_597_870.7) > 16)
            throw new InvalidOperationException("The companion should retain its archived separation.");
        Vector3d transverse = relative / relative.Length;
        var near = Observer(epoch, hole.Direction * -1 + transverse * .65, 60 * hole.SchwarzschildRadiusKilometers);
        var far = Observer(epoch.AddSeconds(3600), transverse * -1 + hole.Direction * .4, 16_000_000);
        string path = Path.Combine(directory, "Лебедь X-1.stscene");
        ScenarioFile.Save(path, new([near, far], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path) || !await scene.ReloadAddOns() || session[0] != near || session[1] != far)
            throw new InvalidOperationException("The two compact object observations must survive reload.");
        var left = scene.Renderer(0);
        var right = scene.Renderer(1);
        if (!left.CompactLensVisible || !right.CompactLensVisible || !right.CompactCompanionVisible ||
            left.CompactSkyCaptures == 0 || right.CompactSkyCaptures == 0)
            throw new InvalidOperationException("The ray shader, sky cube and neighboring resolved star must be visible.");
        using var lens = await Capture();
        if (lens.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not save the compact object views.");
        using var changed = await Left();
        left.CompactDiskEmission = false;
        scene.Seek(0, epoch);
        using var unlit = await Left();
        left.CompactDiskEmission = true;
        left.CompactLensAllowed = false;
        scene.Seek(0, epoch);
        using var plain = await Left();
        int pixels = 0, disk = 0;
        Vector2I middle = changed.GetSize() / 2;
        for (int y = middle.Y - 180; y < middle.Y + 180; y += 2)
        for (int x = middle.X - 180; x < middle.X + 180; x += 2)
        {
            Color a = changed.GetPixel(x, y), b = plain.GetPixel(x, y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .035)
                pixels++;
            Color noEmission = unlit.GetPixel(x, y);
            if (a.B - a.R > .025 && a.B - noEmission.B > .025)
                disk++;
        }
        left.CompactLensAllowed = true;
        scene.Seek(0, epoch);
        Color center = changed.GetPixel(middle.X, middle.Y);
        if (pixels < 120 || disk < 40 || center.R + center.G + center.B > .06)
            throw new InvalidOperationException($"The compact lens only changed {pixels} samples and emitted {disk} disk samples with shadow center {center}.");
        GD.Print($"Compact lens: original Cygnus X-1 and HDE 226868, 60 Schwarzschild radii and 16 million km, {pixels} altered GPU samples and {disk} disk samples, independent reload.");

        string profile = Path.Combine(directory, "cinematic-legacy.json");
        File.WriteAllText(profile, """{"Site":{},"CinematicBlackHoles":true}""");
        var imported = LegacyObserverProfiles.Read(profile, near, session.DeltaT).Observer;
        var cinematic = near with { Sky = imported.Sky };
        var modes = new SimulationScenario([cinematic, near], false, 0, 0, session.DeltaT);
        string modesPath = Path.Combine(directory, "cygnus-disk-modes.stscene");
        ScenarioFile.Save(modesPath, modes);
        if (!scene.LoadScenario(modesPath) || !await scene.ReloadAddOns() ||
            !session.CaptureScenario(0).Views.SequenceEqual(modes.Views))
            throw new InvalidOperationException("The archived cinematic mode must survive reload in only one view.");
        await WaitForFrame();
        using var stylized = left.GetViewport().GetTexture().GetImage();

        int different = 0;
        Vector2I stylizedCenter = stylized.GetSize() / 2, physicalCenter = changed.GetSize() / 2;
        for (int y = -110; y <= 110; y += 2)
        for (int x = -110; x <= 110; x += 2)
        {
            Color a = stylized.GetPixel(stylizedCenter.X + x, stylizedCenter.Y + y);
            Color b = changed.GetPixel(physicalCenter.X + x, physicalCenter.Y + y);
            if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > .07)
                different++;
        }
        if (different < 200)
            throw new InvalidOperationException($"The two Cygnus X-1 modes differed in only {different} sampled pixels.");
        using var comparison = GetViewport().GetTexture().GetImage();
        if (comparison.SavePng(Path.ChangeExtension(modesPath, ".png")) != Error.Ok)
            throw new IOException("Could not save the two compact lens modes.");
        GD.Print($"Cinematic lens GPU: original Cygnus X-1 modes differ in {different} samples after reload.");

        ObserverView Observer(JulianInstant at, Vector3d outward, double distance)
        {
            outward /= outward.Length;
            Vector3d north = new Vector3d(0, 0, 1) - outward * outward.Z;
            return new(at, CameraMode.Solar, id, false)
            {
                Navigation = new NavigationCamera(compact.PositionKilometers, distance, 0, 0, id, true)
                    { Placement = new NavigationPlacement(outward * distance, outward * -1, north / north.Length) },
                Sky = new(true, false, false, false) { DeepSky = true }
            };
        }

        async Task WaitForFrame()
        {
            for (int frame = 0; frame < 2; frame++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
        }

        async Task<Image> Capture()
        {
            await WaitForFrame();
            return GetViewport().GetTexture().GetImage();
        }

        async Task<Image> Left()
        {
            await WaitForFrame();
            return left.GetViewport().GetTexture().GetImage();
        }
    }
}
