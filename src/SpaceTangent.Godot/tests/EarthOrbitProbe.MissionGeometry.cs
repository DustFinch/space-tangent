// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseMissionDisplay(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var jason = new ObjectId("norad/26997");
        var earth = new ObjectId("sol/earth");
        var epoch = JulianInstant.FromJulianDate(2455755, TimeScale.Tdb);
        var world = session.World.Graph;
        if (!world.Evaluate(epoch).TryState(jason, out var state))
            throw new InvalidOperationException("The original Jason-1 is unavailable.");
        var pose = new NavigationCamera(state.PositionKilometers, 4500, .48, .14, jason, true);
        var active = new ObserverView(epoch, CameraMode.Solar, jason, false)
            { Navigation = pose, Sky = new() { Labels = true } };
        var hidden = active with { Sky = active.Sky with { HideOrbits = true } };
        string path = Path.Combine(directory, "archived-display.stscene");
        ScenarioFile.Save(path, new([active, hidden], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("The two trail views did not open.");
        Check();
        if (!await scene.ReloadAddOns())
            throw new InvalidOperationException("The archived display pack could not reload.");
        Check();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the archived orbit style.");
        GD.Print("Archived display: Jason-1 label tint, 45-minute trail, 10-minute lead and independent orbit toggle verified after reload.");

        void Check()
        {
            var shown = scene.Renderer(0);
            var other = scene.Renderer(1);
            if (!shown.MissionTrailVisible || other.MissionTrailVisible || shown.MissionTrailPointCount != 65 ||
                shown.BodyLabel(jason) is not { Visible: true } label ||
                Math.Abs(label.Modulate.R - .7) > 1e-6 || Math.Abs(label.Modulate.B - .9) > 1e-6 ||
                shown.MissionTrailFirstPoint is not { } first || shown.MissionTrailLastPoint is not { } last ||
                (first - OffsetAt(epoch.AddSeconds(-2700))).Length > 1e-6 ||
                (last - OffsetAt(epoch.AddSeconds(600))).Length > 1e-6 ||
                shown.MissionTrailEdgeColors is not { } colors ||
                !colors.First.IsEqualApprox(new Color(.12f, .32f, .18f)) ||
                !colors.Last.IsEqualApprox(new Color(.3f, .8f, .45f)))
                throw new InvalidOperationException("The archived orbit window, fade or label tint differs.");
        }

        Vector3d OffsetAt(JulianInstant time)
        {
            var snapshot = session.World.Graph.Evaluate(time);
            if (!snapshot.TryState(jason, out var satellite) || !snapshot.TryState(earth, out var parent))
                throw new InvalidOperationException("The reference state for the orbit trail is missing.");
            return (satellite - parent).PositionKilometers;
        }
    }

    private async Task ExerciseFollowSpin(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var witness = new ObjectId("mission/jason-spin-witness");
        var offset = new ObjectId("mission/jason-spin-offset");
        var jason = new ObjectId("norad/26997");
        var first = JulianInstant.FromJulianDate(2455755, TimeScale.Tdb);
        var second = first.AddSeconds(600);
        string path = Path.Combine(directory, "archived-follow-spin.stscene");
        var views = new[] { Observer(first), Observer(second) };
        ScenarioFile.Save(path, new(views, false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("The archived spin views did not open.");
        Check();
        var before = session.CaptureScenario(0);
        if (!await scene.ReloadAddOns() || !session.CaptureScenario(0).Views.SequenceEqual(before.Views) ||
            session.DeltaT != before.DeltaT)
            throw new InvalidOperationException("Reloading the archived spin lost the observer state.");
        Check();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the archived spin views.");
        GD.Print("Archived follow spin: original 28-surface model, quarter turn in 600 s and moving offset verified after reload.");

        ObserverView Observer(JulianInstant epoch)
        {
            if (!session.World.Graph.Evaluate(epoch).TryState(witness, out var state))
                throw new InvalidOperationException("The archived spin witness is unavailable.");
            var pose = new NavigationCamera(state.PositionKilometers, .045, .48, .14, witness, true);
            return new(epoch, CameraMode.Solar, witness, false) { Navigation = pose };
        }

        void Check()
        {
            Vector3d[] axes = new Vector3d[2];
            for (int index = 0; index < axes.Length; index++)
            {
                var epoch = session[index].Epoch;
                var world = session.World.Graph.Evaluate(epoch);
                if (!world.TryOrientation(witness, out var attitude) ||
                    attitude.AngularVelocityRadiansPerSecond is not { } omega ||
                    Math.Abs(omega.Length - Math.Tau / 2400) > 1e-10 ||
                    !world.TryMissionReferenceFrame(witness, false, epoch, out _, out var reference) ||
                    !world.TryState(offset, out var marker) || !world.TryState(jason, out var center) ||
                    Math.Abs((marker - center).PositionKilometers.Length - Math.Sqrt(140000)) > 1e-6 ||
                    scene.Renderer(index).ImportedBodyNode(witness) is not { Visible: true, Mesh: ArrayMesh mesh } ||
                    mesh.GetSurfaceCount() != 28)
                    throw new InvalidOperationException("The archived spin model or its offset was not restored.");
                axes[index] = reference.ToWorld.Transpose().Transform(attitude.ToWorld.X);
            }
            if (Math.Abs(Vector3d.Dot(axes[0], axes[1])) > 1e-6)
                throw new InvalidOperationException("The archived model must turn by a quarter revolution relative to Jason-1.");
        }
    }

    private async Task ExerciseCompoundMission(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var enceladus = new ObjectId("mission/enceladus");
        var beacon = new ObjectId("mission/combinationbeacon");
        var epoch = JulianInstant.FromJulianDate(2453187.625, TimeScale.Tdb);
        var snapshot = session.World.Graph.Evaluate(epoch);
        if (!snapshot.TryState(beacon, out var state))
            throw new InvalidOperationException("The archived weighted orbit was not installed.");

        var pose = new NavigationCamera(state.PositionKilometers, 250, .48, .14, beacon, true);
        var observer = new ObserverView(epoch, CameraMode.Solar, beacon, false) { Navigation = pose };
        string path = Path.Combine(directory, "saturn-compound-orbit.stscene");
        ScenarioFile.Save(path, new([observer], false, 0, 0, session.DeltaT));
        if (!scene.LoadScenario(path))
            throw new InvalidOperationException("The archived weighted-orbit view did not open.");
        Check();
        if (!await scene.ReloadAddOns() || session[0] != observer)
            throw new InvalidOperationException("Reloading the archived pack lost the mission view.");
        Check();

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.ChangeExtension(path, ".png")) != Error.Ok)
            throw new IOException("Could not capture the archived weighted-orbit view.");
        GD.Print("Archived Saturn pack: three polynomial orbits and weighted beacon active after reloading.");

        void Check()
        {
            var current = session.World.Graph.Evaluate(epoch);
            if (!current.TryState(enceladus, out var center) || !current.TryState(beacon, out var combined) ||
                !current.TryState(new("mission/dione"), out _) || !current.TryState(new("mission/phoebe"), out _) ||
                ((combined - center).PositionKilometers - new Vector3d(1980, -40, -60)).Length > 2e-6)
                throw new InvalidOperationException("The archived Saturn states or weighted relative position differ.");
        }
    }

    private async Task ExerciseMissionGeometry(SolarSystemScene scene, string directory)
    {

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_GEOMETRY_PROBE") is not ("1" or "only"))
            return;
        var session = scene.Session!;
        var id = new ObjectId("mission/timeswitchmodel");
        var model = scene.Renderer(0).ImportedBodyNode(id);
        if (session.World.Catalogue.Find(id) is null || model is null)
            throw new InvalidOperationException("The original timed mesh fixture is unavailable.");
        await Check("before", 2455754.99, null);
        ArrayMesh? first = await Check("first", 2455755.005, 28);
        ArrayMesh? nested = await Check("nested", 2455755.015, 20);
        if (ReferenceEquals(first, nested))
            throw new InvalidOperationException("The nested phase must select the original SORCE mesh.");
        await Check("gap", 2455755.025, null);
        ArrayMesh? recovered = await Check("recovered", 2455755.035, 28);
        if (!ReferenceEquals(first, recovered) || !await scene.ReloadAddOns() ||
            !scene.Renderer(0).ImportedBodyNode(id)!.Visible)
            throw new InvalidOperationException("Restored geometry or its epoch was lost on add-on reload.");
        await Check("ended", 2455755.045, null);
        GD.Print("Original timed geometry rendered: outer and nested meshes, explicit gap, recovery, and end.");

        async Task<ArrayMesh?> Check(string stage, double day, int? surfaceCount)
        {
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            var frame = session.World.Graph.Evaluate(epoch);
            if (!frame.TryState(id, out var state))
                throw new InvalidOperationException("The mission state must remain available even during a geometry gap.");
            var pose = new NavigationCamera(state.PositionKilometers, .055, .48, .14, id, true);
            var observer = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose };
            string path = Path.Combine(directory, "mission-geometry-" + stage);
            ScenarioFile.Save(path + ".stscene", new([observer], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path + ".stscene"))
                throw new InvalidOperationException($"Could not load the timed geometry view {stage}.");
            model = scene.Renderer(0).ImportedBodyNode(id);
            var active = model is { Visible: true, Mesh: ArrayMesh mesh } ? mesh : null;
            if (active?.GetSurfaceCount() != surfaceCount || surfaceCount is null && model?.Visible == true)
                throw new InvalidOperationException($"Unexpected timed geometry at {stage}: {active?.GetSurfaceCount()} surfaces.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(path + ".png") != Error.Ok)
                    throw new IOException($"Could not save timed geometry view {stage}.");
            }
            return active;
        }
    }

    private async Task ExerciseMissionMeshScale(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        foreach (var (name, size, direct, offset, surfaces) in new (string, double, double?, Vector3d, int)[]
        {
            ("nativesizeprobe", .01, null, new(1000, 2000, -1500), 28),
            ("nativescaleprobe", 0, 8e-7, new(-500, 250, 400), 20),
            ("nativedefaultprobe", 0, 1, default, 28)
        })
        {
            var id = new ObjectId("mission/" + name);
            var epoch = JulianInstant.FromJulianDate(name == "nativedefaultprobe" ? 2455755.2 : 2455755.005, TimeScale.Tdb);
            var snapshot = session.World.Graph.Evaluate(epoch);
            if (!snapshot.TryState(id, out var state) || !snapshot.TryOrientation(id, out var frame))
                throw new InvalidOperationException($"Missing original mission mesh state for {name}.");
            double radius = session.World.TargetRadiusKilometers(id);
            double range = radius * 4;
            var pose = new NavigationCamera(state.PositionKilometers, range, .48, .14, id, true);
            var view = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose };
            string path = Path.Combine(directory, name);
            ScenarioFile.Save(path + ".stscene", new([view], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path + ".stscene") || !await scene.ReloadAddOns() ||
                scene.Renderer(0).ImportedBodyNode(id) is not { Visible: true, Mesh: ArrayMesh mesh } node ||
                mesh.GetSurfaceCount() != surfaces)
                throw new InvalidOperationException($"Original mesh did not render after reload for {name}.");
            var span = mesh.GetAabb().Size;
            double extent = Math.Max(span.X, Math.Max(span.Y, span.Z));
            double scale = direct ?? size * 2 / extent;
            double projected = scale * 48 / range;
            var worldOffset = frame.ToWorld.Transform(offset) * projected;
            var expected = new Vector3((float)worldOffset.X, (float)worldOffset.Z, (float)-worldOffset.Y);
            if (Math.Abs(node.Basis.X.Length() / projected - 1) > 1e-6 ||
                (node.Position - expected).Length() > 2e-5)
                throw new InvalidOperationException($"Wrong mesh scale or offset for {name}: {node.Position} vs {expected}.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.SavePng(path + ".png") != Error.Ok)
                throw new IOException("Could not save the mission mesh scale capture.");
            GD.Print(FormattableString.Invariant($"Mesh scale {name}: {scale:R}, offset {node.Position}, navigation radius {radius:R}."));
        }
    }


    private async Task ExerciseTimedGeometryBounds(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var id = new ObjectId("mission/timeboundsprobe");
        const double expected = 0.010251794010400772; // Native float sphere for the original model and authored offset.
        double bound = session.World.TargetRadiusKilometers(id);
        if (bound != expected)
            throw new InvalidOperationException($"Expected the hidden original model to set a {expected:R} km bound, got {bound:R} km.");

        foreach (double day in new[] { 2455755.005, 2455755.015, 2455755.025 })
        {
            var epoch = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            if (!session.World.Graph.Evaluate(epoch).TryState(id, out var state))
                throw new InvalidOperationException("The timed geometry target is unavailable.");
            var pose = new NavigationCamera(state.PositionKilometers, bound * 2, .48, .14, id, true);
            var view = new ObserverView(epoch, CameraMode.Solar, id, false) { Navigation = pose };
            string path = Path.Combine(directory, FormattableString.Invariant($"timed-bounds-{day:F3}"));
            ScenarioFile.Save(path + ".stscene", new([view], false, 0, 0, session.DeltaT));
            if (!scene.LoadScenario(path + ".stscene"))
                throw new InvalidOperationException("Could not open the timed geometry scene.");

            var node = scene.Renderer(0).ImportedBodyNode(id);
            bool globe = day == 2455755.015;
            if (node is not { Visible: true } || (globe ? node.Mesh is not SphereMesh : node.Mesh is not ArrayMesh))
                throw new InvalidOperationException("The hidden mesh must leave the active SORCE and globe phases intact.");

            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(path + ".png") != Error.Ok)
                    throw new IOException("Could not capture the timed geometry phase.");
            }
        }

        if (!await scene.ReloadAddOns() || session.World.TargetRadiusKilometers(id) != bound)
            throw new InvalidOperationException("The timed navigation bound changed after add-on reload.");
        GD.Print(FormattableString.Invariant($"Timed geometry kept a {bound:R} km navigation bound across both meshes and the globe."));
    }
}
