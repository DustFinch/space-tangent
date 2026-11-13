// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private void ExerciseLegacyFrames(string follow)
    {
        var earth = SolarSystemSample.EarthId;
        var sun = SolarSystemSample.SunId;
        var moon = SolarSystemSample.MoonId;
        var sync = SceneLink.Read(follow.Replace("Follow/", "SyncOrbit/", StringComparison.Ordinal), Session.World, Session.DeltaT).Scenario.Views[0];
        Require(sync.Navigation is { Target: { } rotating, Placement.BodyFixed: true } && rotating == earth,
            "The synchronously orbiting link must retain a local Earth frame.");
        var chase = SceneLink.Read(follow.Replace("Follow/", "Chase/", StringComparison.Ordinal), Session.World, Session.DeltaT).Scenario.Views[0];
        var phase = SceneLink.Read(follow.Replace("Follow/Sol:Earth/", "PhaseLock/Sol:Earth/Sol:Earth:Moon/", StringComparison.Ordinal),
            Session.World, Session.DeltaT).Scenario.Views[0];
        var world = Session.World.Graph.Evaluate(chase.Epoch);
        Require(world.TryState(earth, out var e) && world.TryState(sun, out _) && world.TryState(moon, out _),
            "The original frame test requires Earth, Sun and Moon ephemerides.");
        world.TryState(sun, out var s);
        world.TryState(moon, out var m);
        Require(chase.Navigation is { Placement: { Frame: NavigationFrame.Chase, Reference: { } parent } } && parent == sun &&
            phase.Navigation is { Placement: { Frame: NavigationFrame.PhaseLock, Reference: { } partner } } && partner == moon,
            "Chase uses the parent, while phase lock uses the linked second body.");
        var chaseDirection = chase.Navigation!.Value.ResolvePlacement(world).OffsetKilometers;
        var phaseDirection = phase.Navigation!.Value.ResolvePlacement(world).OffsetKilometers;
        var velocity = (s - e).VelocityKilometersPerSecond;
        var position = (m - e).PositionKilometers;
        Require(Vector3d.Dot(chaseDirection / chaseDirection.Length, velocity / velocity.Length) > .999999 &&
            Vector3d.Dot(phaseDirection / phaseDirection.Length, position / position.Length) > .999999,
            "The local X axes must follow parent-relative velocity and partner-relative position, respectively.");
        var later = Session.World.Graph.Evaluate(chase.Epoch.AddSeconds(3600));
        Require((phaseDirection - phase.Navigation.Value.ResolvePlacement(later).OffsetKilometers).Length > 100,
            "Advancing the epoch must rotate the phase-lock camera in the same local coordinates.");
        var original = new SimulationScenario([phase], true, 0, 0, Session.DeltaT);
        var restored = SceneLink.Read(SceneLink.Write(original), Session.World, Session.DeltaT).Scenario;
        RequireScenario(original, restored, "Round-tripping the frame and reference in a native link");
        foreach (var (mode, referenceView) in new[] { ("SyncOrbit", sync), ("Chase", chase), ("PhaseLock", phase) })
        {
            string exported = SceneLink.WriteLegacy(referenceView, Session.World, Session.DeltaT);
            Require(exported.StartsWith($"cel://{mode}/", StringComparison.Ordinal), "The legacy export must keep its frame name.");
            var roundTrip = SceneLink.Read(exported, Session.World, Session.DeltaT).Scenario.Views[0];
            NavigationPlacement expectedPose = referenceView.Navigation!.Value.ResolvePlacement(world, Session.DeltaT);
            NavigationPlacement restoredPose = roundTrip.Navigation!.Value.ResolvePlacement(world, Session.DeltaT);
            Require(roundTrip.Epoch == referenceView.Epoch && roundTrip.Selection == referenceView.Selection &&
                (restoredPose.OffsetKilometers - expectedPose.OffsetKilometers).Length < .0001 &&
                Vector3d.Dot(restoredPose.Forward, expectedPose.Forward) > .999999,
                "The legacy export must preserve the epoch, selection, position and orientation within encoding precision.");
        }
    }

    private async Task ExerciseViewFrames(string directory, string bookmarks)
    {
        if (Session.ViewCount == 1)
            await KeyPress(Key.U, ctrl: true);
        Require(Session.ViewCount == 2, "The frame regression needs two views.");
        if (Session.Synchronized)
            Session.Synchronize(false, 0);
        _scene.Seek(0, JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb));
        _scene.Seek(1, JulianInstant.FromJulianDate(2460601.5008, TimeScale.Tdb));
        Session.SelectObject(0, PlanetCatalogue.MarsId);
        Require(Session.ToggleTracking(0), "The Mars orbit pose must be available.");
        await Click(Find<SubViewportContainer>("ViewCanvas0"));
        var settings = Find<Button>("SettingsTab");
        if (!settings.ButtonPressed)
            await Click(settings);
        await Click(Find<Button>("FollowBodyRotation"));
        Require(Session[0].Navigation is { Placement.BodyFixed: true }, "The selected Mars pose must follow the rotating body frame.");
        NavigationCamera mars = Session[0].Navigation!.Value;
        Require(Session.AdjustCamera(0, .3, -.16, 1), "A rotation-following camera must allow manual orbiting.");
        NavigationCamera orbited = Session[0].Navigation!.Value;
        Vector3d originalOffset = mars.Placement!.Value.OffsetKilometers;
        Vector3d rotatedOffset = orbited.Placement!.Value.OffsetKilometers;
        double distance = originalOffset.Length;
        Require((originalOffset - rotatedOffset).Length > distance * .01 &&
            Math.Abs(rotatedOffset.Length / distance - 1) < 1e-10,
            $"Orbiting must move the eye at constant distance ({distance:R} to {rotatedOffset.Length:R} km).");
        mars = orbited;
        var before = mars.ResolvePlacement(Session.World.Graph.Evaluate(Session[0].Epoch));
        await Click(Find<Button>("AdvanceHour0"));
        NavigationCamera rotated = Session[0].Navigation!.Value;
        var after = rotated.ResolvePlacement(Session.World.Graph.Evaluate(Session[0].Epoch));
        Require(mars.Placement == rotated.Placement && (before.Forward - after.Forward).Length > .001,
            "A time step must rotate the inertial orientation while preserving the local frame.");

        await Click(Find<SubViewportContainer>("ViewCanvas1"));
        await Click(Find<Button>("FreeFlight"));
        var at = Session[1].Navigation;
        using (var down = new InputEventKey { Keycode = Key.W, Pressed = true })
        using (var up = new InputEventKey { Keycode = Key.W })
        {
            Tree.Root.PushInput(down, inLocalCoords: true);
            _scene._Process(.05);
            Tree.Root.PushInput(up, inLocalCoords: true);
        }
        Require(Session[1].Navigation is { Tracking: false, Placement.BodyFixed: false } moved &&
            moved.AnchorKilometers != at!.Value.AnchorKilometers, "A free-flight key must translate the active camera.");
        NavigationCamera freeStart = Session[1].Navigation!.Value;
        Session.SelectObject(1, SolarSystemSample.EarthId);
        Require(Session.GoToSelection(1) && Session[1].Navigation?.Placement == freeStart.Placement &&
            Session[1].Navigation?.AnchorKilometers == freeStart.AnchorKilometers,
            "A flight must begin at the actual free-flight eye and attitude.");
        var snapshot = Session.World.Graph.Evaluate(Session[1].Epoch);
        Session.UpdateCamera(1, snapshot, .016);
        NavigationCamera transition = Session[1].Navigation!.Value;
        Require(transition.IsValid() && transition.Placement is { } attitude &&
            Vector3d.Dot(attitude.Forward, freeStart.Placement!.Value.Forward) > .999,
            "The first flight step must preserve a continuous and valid attitude.");
        Require(Session.CancelFlight(1) && Session[1].Navigation == transition,
            "Cancelling a flight must preserve its interpolated pose.");
        Session.StartFreeFlight(1);
        await Click(Find<Button>("StackViews"));
        Require(Find<BoxContainer>("Views").Vertical && Session.SplitLayout == ViewSplit.Stacked,
            "The alternate layout must stack both live views.");
        Find<SpinBox>("ViewField0").Value = 28;
        Find<SpinBox>("ViewField1").Value = 67;
        var expected = Session.CaptureScenario(1);
        Require(expected.Views[0].FieldOfViewDegrees == 28 && expected.Views[1].FieldOfViewDegrees == 67 &&
            expected.Views[0].Epoch != expected.Views[1].Epoch, "Each view must retain its own lens and clock.");

        var scenarios = Find<Button>("BookmarksTab");
        if (!scenarios.ButtonPressed)
            await Click(scenarios);
        await FillField("BookmarkName", "Mars and free flight");
        await Click(Find<Button>("StoreBookmark"));
        Require(new ObserverBookmarkFile(bookmarks).Load().Single().Name == "Mars and free flight",
            "The named bookmark must reach persistent storage.");
        await Click(Find<Button>("CreateSceneLink"));
        var link = Find<LineEdit>("SceneLinkInput");
        string native = link.Text;
        Require(native.StartsWith(SceneLink.Scheme, StringComparison.Ordinal), "The scene must have a portable link.");

        const string follow = "cel://Follow/Sol:Earth/2024-04-09T00:00:00.00000?x=AAAAAAAAAAAB&y=&z=&ow=1&ox=0&oy=0&oz=0&select=Sol:Earth:Moon&fov=45&ts=1&ltd=0&p=1&ver=3";
        link.Text = follow + "&rf=1";
        await Click(Find<Button>("OpenSceneLink"));
        Require(Session.ViewCount == 1 && Session[0].Selection == SolarSystemSample.MoonId &&
            Session[0].Navigation is { Tracking: true, Target: { } parent, Placement: { } pose } &&
            parent == SolarSystemSample.EarthId && Math.Abs(pose.OffsetKilometers.X - 9_460_730.4725808) < 1e-5 &&
            Math.Abs(pose.Forward.Y - .917482) < .000001 && Session[0].FieldOfViewDegrees == 45 &&
            Session[0].Epoch == JulianInstant.FromJulianDate(2460409.5, TimeScale.Tdb) &&
            Find<Label>("WorkspaceStatus").Text.Contains("Unapplied link parameters: rf", StringComparison.Ordinal),
            "The reference link must restore selection, time, field and orientation and report its unsupported flag.");
        var imported = Session.CaptureScenario(0);
        ExerciseLegacyFrames(follow);
        var free = SceneLink.Read(follow.Replace("Follow/Sol:Earth", "Freeflight", StringComparison.Ordinal)
            .Replace("ver=3", "ver=4", StringComparison.Ordinal), Session.World, Session.DeltaT).Scenario.Views[0];
        Require(free.Navigation is { Tracking: false, Target: null, Placement: { } independent } &&
            independent.OffsetKilometers == default && free.FieldOfViewDegrees == 45 && free.Epoch == Session[0].Epoch,
            "A version 4 free-flight link must restore its independent eye and field.");
        link.Text = follow.Replace("Sol:Earth/", "Sol:Missing/", StringComparison.Ordinal);
        await Click(Find<Button>("OpenSceneLink"));
        RequireScenario(imported, Session.CaptureScenario(0), "An unavailable linked object");
        Require(Find<Label>("WorkspaceStatus").Text.Contains("unavailable object", StringComparison.Ordinal),
            "A missing linked object must produce a visible diagnostic.");

        await Click(Find<Button>("RestoreBookmark"));
        RequireScenario(expected, Session.CaptureScenario(1), "Opening the named bookmark");
        link.Text = native;
        await Click(Find<Button>("OpenSceneLink"));
        RequireScenario(expected, Session.CaptureScenario(1), "Opening the native scene link");
        Require(Session.SplitLayout == ViewSplit.Stacked && Find<BoxContainer>("Views").Vertical,
            "Opening the link must also restore the split direction.");
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        Require(Find<SpinBox>("ViewField0").GetLineEdit().Text.StartsWith("28", StringComparison.Ordinal) &&
            Find<SpinBox>("ViewField1").GetLineEdit().Text.StartsWith("67", StringComparison.Ordinal),
            "Both visible field controls must display their restored values.");
        Require(await _scene.ReloadAddOns(), "Reload must succeed with a linked two-view scene.");
        RequireScenario(expected, Session.CaptureScenario(1), "Reloading the linked two-view scene");
        const string filename = "saved-view-frames.stscene";
        await UseScenarioDialog("SaveScenario", 1, directory, filename);
        var saved = ScenarioFile.Load(Path.Combine(directory, filename));
        RequireScenario(expected, saved, "Saving frames and field of view");
        Require(saved.SplitLayout == ViewSplit.Stacked, "The saved file must keep the view layout.");
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_VIEW_FRAME_SCENE") is { Length: > 0 } output)
            File.Copy(Path.Combine(directory, filename), output, overwrite: true);
        GD.Print("UI: restored rotation, free-flight pose, two fields of view, stacked layout and link after reload");

        const string favorites = "favorites.cel";
        File.WriteAllText(Path.Combine(directory, favorites), """
            # Named observer records
            "Science" { isFolder true parentFolder "" }
            "Lunar flyby" {
                isFolder false parentFolder "Science"
                base [ 0 0 0 ] offset [ 1 0 0 ]
                axis [ 1 0 0 ] angle 0 time 2460409.5
                selection "Sol/Earth/Moon" coordsys "geographic"
            }
            "Outside the ephemeris" { isFolder false time 0 }
            """);
        await UseScenarioDialog("OpenScenario", 0, directory, favorites);
        var entries = new ObserverBookmarkFile(bookmarks).Load();
        Require(entries.Count == 2 && entries[1].Name == "Science / Lunar flyby" &&
            Find<Label>("WorkspaceStatus").Text.Contains("Imported 1 favorites (1 skipped)", StringComparison.Ordinal),
            "The existing file dialog must import favorites while preserving prior bookmarks.");
        RequireScenario(expected, Session.CaptureScenario(1), "Importing favorites without replacing the active scenario");
        await Click(Find<Button>("RestoreBookmark"));
        var favorite = SceneLink.Read(entries[1].Link, Session.World, Session.DeltaT).Scenario.Views[0];
        var states = Session.World.Graph.Evaluate(favorite.Epoch);
        Require(states.TryState(SolarSystemSample.SunId, out var sun) &&
            states.TryState(SolarSystemSample.MoonId, out _), "The saved favorite requires Sun and Moon positions.");
        var favoriteCamera = favorite.Navigation!.Value;
        var eye = favoriteCamera.AnchorKilometers + favoriteCamera.ResolvePlacement(states, Session.DeltaT).OffsetKilometers;
        Require(Session.ViewCount == 1 && Session[0].Selection == SolarSystemSample.MoonId &&
            favoriteCamera.Placement is { BodyFixed: true } && favorite.Epoch == imported.Views[0].Epoch &&
            Session[0].Navigation?.Placement == favoriteCamera.Placement &&
            Session[0].Epoch.CompareTo(favorite.Epoch) >= 0 && Session[0].Epoch.CompareTo(favorite.Epoch.AddSeconds(1)) < 0 &&
            (eye - sun.PositionKilometers - new Vector3d(9_460_730.4725808, 0, 0)).Length < 1e-5,
            "Opening the imported favorite must restore the named body, absolute eye, local frame and epoch.");
        GD.Print("UI: imported favorites through the existing file dialog and restored a Moon-fixed observer");
        await ExerciseArchivedObserver(directory);
    }

    private async Task ExerciseArchivedObserver(string directory)
    {
        // Exact observer record emitted by the previous Windows build during the Earth acceptance run.
        const string state = """
            {
                "schemaVersion":1,
                "positionConvention":"universal-native-J2000-ecliptic-micro-light-year",
                "positionBits":{"x":"0000000000000002bc9da85020b386a8","y":"fffffffffffffffffff31c5308625ff8","z":"000000000000000fe0df3253352c3c91"},
                "frame":"J2000EclipticYUp",
                "orientationConvention":"active-camera-to-frame-wxyz",
                "orientationBinary64":{"w":"bfe835018edf9385","x":"3fc21b84627b430c","y":"bfe415d3e238c27f","z":"bfbe0c6f1c3e45e7"},
                "time":{"wholeDays":"2453187","nanosecondsOfDay":"54000000000000","scale":"Tdb"},
                "selectedId":"Sol/Earth"
            }
            """;
        const string filename = "old-observer.json";
        File.WriteAllText(Path.Combine(directory, filename), state);
        await UseScenarioDialog("OpenScenario", 0, directory, filename);
        var earth = SolarSystemSample.EarthId;
        var expectedEye = new Vector3d(25_891_944.206882183, -137_824_623.32902697, -59_756_315.26899842);
        Require(Session[0].Selection == earth && Session[0].Navigation is { Tracking: false } free &&
            (free.AnchorKilometers - expectedEye).Length < 1e-6,
            "Opening the archived observer file must restore its absolute eye and selected Earth.");

        byte[] bookmark = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Name = "Earth",
            Observer = state,
            Rate = 0,
            Frame = 1,
            SchemaVersion = 3,
            FieldOfViewDegrees = 42,
            FrameReference = "Sol/Earth"
        });
        var link = Find<LineEdit>("SceneLinkInput");
        link.Text = "celestia-godot:" + Convert.ToBase64String(bookmark).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Click(Find<Button>("OpenSceneLink"));
        var view = Session[0];
        var expectedEpoch = new JulianInstant(2453187, 54_000_000_000_000, TimeScale.Tdb);
        Require(view.Epoch == expectedEpoch && view.Selection == earth && view.FieldOfViewDegrees == 42 &&
            view.Navigation is { Tracking: true, Placement.BodyFixed: true } && !view.Playing,
            "The archived link must restore its epoch, body-fixed frame, playback rate and lens.");
        var pose = view.Navigation!.Value;
        var placement = pose.ResolvePlacement(Session.World.Graph.Evaluate(expectedEpoch), Session.DeltaT);
        Require((pose.AnchorKilometers + placement.OffsetKilometers - expectedEye).Length < 1e-6,
            "Changing the reference frame must keep the original archived eye.");
        GD.Print("UI: restored the archived Earth observer and its fixed-frame bookmark through existing entry points");
        var moon = SolarSystemSample.MoonId;
        var beforeCollection = Session.CaptureScenario(0);
        const string collection = "Архивные закладки.json";
        const string label = "Лунный экватор";
        object Entry(string name, int frame) => new
        {
            Name = name, Observer = state, Rate = 0, Frame = frame, SchemaVersion = 3,
            FieldOfViewDegrees = 38, FrameReference = "Sol/Earth/Moon"
        };
        File.WriteAllBytes(Path.Combine(directory, collection),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[] { Entry(label, 5), Entry(label, 5), Entry("invalid", 999) }));
        await UseScenarioDialog("OpenScenario", 0, directory, collection);
        RequireScenario(beforeCollection, Session.CaptureScenario(0), "Importing the archived collection");
        var archivedList = Find<OptionButton>("SavedBookmarks");
        Require(archivedList.GetItemText(archivedList.Selected) == label &&
            Find<Label>("WorkspaceStatus").Text.Contains("Imported 1 favorites (2 skipped)", StringComparison.Ordinal),
            "The archived collection must preserve the Unicode name and report invalid or repeated records.");
        await Click(Find<Button>("RestoreBookmark"));
        var mean = Session[0];
        Require(mean.Selection == earth && mean.FieldOfViewDegrees == 38 && !mean.Playing &&
            mean.Navigation is { Tracking: true, Placement: { Frame: NavigationFrame.Equatorial } } && mean.Navigation.Value.Target == moon,
            "The collection must restore the mean-equator Moon frame independently of the Earth selection.");
        var meanCamera = mean.Navigation!.Value;
        var current = meanCamera.ResolvePlacement(Session.World.Graph.Evaluate(expectedEpoch), Session.DeltaT);
        Require((meanCamera.AnchorKilometers + current.OffsetKilometers - expectedEye).Length < 1e-6,
            "Importing the legacy mean-equator frame must preserve the archived absolute eye.");
        string planets = System.Environment.GetEnvironmentVariable("SPACETANGENT_PLANET_TEXTURES") ??
            throw new InvalidOperationException("The mean-equator check needs the original PCK directory.");
        var pck = new SpicePolynomialBodyFrame(Path.Combine(planets, "pck00011.tpc"), 301);
        var sixHours = expectedEpoch.AddSeconds(21600);
        var meanAxes = pck.SampleMeanEquator(sixHours).ToWorld;
        var local = meanCamera.Placement!.Value;
        var laterPlacement = meanCamera.ResolvePlacement(Session.World.Graph.Evaluate(sixHours), Session.DeltaT);
        Require((laterPlacement.Forward - meanAxes.Transform(local.Forward)).Length < 1e-11 &&
            (laterPlacement.OffsetKilometers - meanAxes.Transform(local.OffsetKilometers)).Length < 1e-7,
            "The restored lunar equator must follow the native pole while omitting daily spin.");
        Require(await _scene.ReloadAddOns() && Session[0] == mean,
            "The imported observer frame must survive a generation replacement.");
        await Click(Find<Button>("OpenSceneLink"));
        RequireScenario(beforeCollection, Session.CaptureScenario(0), "Reopening the fixed Earth bookmark");
        GD.Print("UI: imported a legacy collection with the archived camera, preserved Unicode and restored the lunar mean-equator frame.");

        Session.SelectObject(0, moon);
        var snapshot = Session.World.Graph.Evaluate(expectedEpoch);
        Require(snapshot.TryState(moon, out var lunarState), "Centering the Moon needs its ephemeris.");
        await KeyPress(Key.C);
        Require(Session.IsFlying(0), "Centering must start a turn in the existing view.");
        Session.UpdateCamera(0, snapshot, .21);
        var halfway = Session[0].Navigation!.Value;
        var attitude = halfway.ResolvePlacement(snapshot, Session.DeltaT);
        Vector3d lunarDirection = lunarState.PositionKilometers - expectedEye;
        Require((halfway.AnchorKilometers + attitude.OffsetKilometers - expectedEye).Length < 1e-6 &&
            Vector3d.Dot(attitude.Forward, lunarDirection / lunarDirection.Length) < .9999 &&
            Vector3d.Dot(attitude.Forward, placement.Forward) < .9999,
            "The intermediate attitude must turn without moving the eye or jumping to the end.");
        await Until(() => !Session.IsFlying(0), "Centering the Moon from the fixed Earth frame");
        var fixedView = Session[0].Navigation!.Value;
        var fixedPose = fixedView.ResolvePlacement(snapshot, Session.DeltaT);
        var sight = lunarState.PositionKilometers - expectedEye;
        Require(fixedView.Target == earth && fixedView.Placement is { BodyFixed: true } &&
            (fixedView.AnchorKilometers + fixedPose.OffsetKilometers - expectedEye).Length < 1e-6 &&
            Vector3d.Dot(fixedPose.Forward, sight / sight.Length) > .999999999,
            "Centering the Moon from the Earth frame must preserve that frame and the camera eye.");
        Session.StartFreeFlight(0);
        await KeyPress(Key.C);
        Require(Session[0].Navigation is { Tracking: true, Target: { } target } && target == moon,
            "Centering from free flight must keep the Moon centered as time advances.");
        await Until(() => !Session.IsFlying(0), "Centering the Moon from free flight");
        _scene.Advance(0, 3600);
        var tracked = Session[0].Navigation!.Value;
        var later = Session.World.Graph.Evaluate(Session[0].Epoch);
        Require(later.TryState(moon, out var moved) && tracked.AnchorKilometers == moved.PositionKilometers &&
            Vector3d.Dot(tracked.Placement!.Value.Forward, sight / sight.Length) > .999999999,
            "Stepping time must keep the focused Moon centered.");
        GD.Print("UI: smoothly centered the Moon without moving the eye and retained both kinds of tracking across time");
    }
}
