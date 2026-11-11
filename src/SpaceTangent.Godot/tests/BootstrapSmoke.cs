// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class BootstrapSmoke : Node
{
    public override async void _Ready()
    {
        try
        {
            Assembly.Load("SpaceTangent.Core");
            Assembly.Load("SpaceTangent.Contracts");
            var scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(scene);
            var startup = System.Diagnostics.Stopwatch.StartNew();
            while (!scene.InitializationFinished)
            {
                Require(startup.Elapsed.TotalSeconds < 20, "World initialization timed out.");
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
            {
                CheckWorkspacePanels(scene);
                CheckSimulation(scene);
            }
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
            {
                await CheckSatellitesAndReload(scene);
                if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_TEXTURES")))
                    await CheckScenarios(scene);
            }
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_CATALOG")))
                await CheckMission(scene);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_PAUSE_SCRIPT") is { Length: > 0 } script)
                await CheckLua(scene, script);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_TIMELINE_ROOT")))
                await CheckTimeline(scene);
            if (scene.Session?.World.Probe is not null)
                await CheckProbe(scene);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TEST_SSC_OVERLAYS") is { Length: > 0 } overlays)
                await CheckSscOverlays(scene, overlays);
            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_TERRAIN_ROOT")))
                await CheckLunarTerrain(scene);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TEST_CACHE_TIMELINE") is { Length: > 0 } cacheRoot)
                await CheckAddonCache(scene, cacheRoot);

            if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT")))
                await CheckNavigation(scene);
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_WORKSPACE_CAPTURE") is { Length: > 0 } workspace)
                await CaptureWorkspace(scene, workspace);

            GD.Print("SpaceTangent bootstrap smoke passed");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            GetTree().Quit(1);
        }
    }

    private async Task CaptureWorkspace(SolarSystemScene scene, string path)
    {
        SimulationSession session = scene.Session!;
        if (session.ViewCount == 2)
            scene.ToggleSplit();
        session.SetPlaying(0, false);
        scene.ShowFullMoon(0);
        var searchTab = (Button)scene.FindChild("SearchTab", recursive: true, owned: false);
        if (!searchTab.ButtonPressed)
            searchTab.EmitSignal(Button.SignalName.Pressed);
        var query = (LineEdit)scene.FindChild("ObjectQuery", recursive: true, owned: false);
        query.Text = "Moon";
        query.EmitSignal(LineEdit.SignalName.TextChanged, query.Text);
        query.EmitSignal(LineEdit.SignalName.TextSubmitted, query.Text);
        query.Text = "";
        query.EmitSignal(LineEdit.SignalName.TextChanged, query.Text);
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ACCEPTANCE_SCENES") is { Length: > 0 } scenes)
            Require(scene.SaveScenario(Path.Combine(scenes, "workspace.stscene")), "The workspace showcase must save.");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using Image image = GetViewport().GetTexture().GetImage();
        Require(image.SavePng(path) == Error.Ok, "Could not save the workspace view.");
    }

    private static void CheckWorkspacePanels(SolarSystemScene scene)
    {
        string[] panels = ["Search", "Time", "Layers", "Location", "Ephemerides", "Optics", "SkyCultures",
            "Bookmarks", "AddOns", "Scripts", "Ship", "Settings", "Help"];
        foreach (string id in panels)
        {
            var tab = (Button)scene.FindChild(id + "Tab", recursive: true, owned: false);
            var page = (VBoxContainer)scene.FindChild(id + "Page", recursive: true, owned: false);
            if (!tab.ButtonPressed)
                tab.EmitSignal(Button.SignalName.Pressed);
            Require(tab.ButtonPressed && page.IsVisibleInTree() && page.GetChildCount() >= 2,
                $"The {id} workspace panel must show its controls.");
        }
        var search = (Button)scene.FindChild("SearchTab", recursive: true, owned: false);
        search.EmitSignal(Button.SignalName.Pressed);
        GD.Print("SpaceTangent workspace panel smoke passed");
    }

    private static void CheckSimulation(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session ?? throw new InvalidOperationException("DE440 failed to load.");
        var searchTab = (Button)scene.FindChild("SearchTab", recursive: true, owned: false);
        var timeTab = (Button)scene.FindChild("TimeTab", recursive: true, owned: false);
        var layersTab = (Button)scene.FindChild("LayersTab", recursive: true, owned: false);
        var sidebar = (Control)scene.FindChild("Sidebar", recursive: true, owned: false);
        Require(searchTab.ButtonPressed && sidebar.IsVisibleInTree(), "The search panel must open at startup.");
        var query = (LineEdit)scene.FindChild("ObjectQuery", recursive: true, owned: false);
        query.Text = "moo";
        query.EmitSignal(LineEdit.SignalName.TextChanged, query.Text);
        query.EmitSignal(LineEdit.SignalName.TextSubmitted, query.Text);
        Require(session[0].Selection == SpaceTangent.Core.Ephemerides.SolarSystemSample.MoonId &&
                session[0].Mode == CameraMode.Solar, "Search must select the Moon without changing the current camera mode.");
        layersTab.EmitSignal(Button.SignalName.Pressed);
        Require(layersTab.ButtonPressed && !searchTab.ButtonPressed, "Only the chosen sidebar tab must be active.");
        layersTab.EmitSignal(Button.SignalName.Pressed);
        Require(!sidebar.IsVisibleInTree(), "Clicking the active tab must close the sidebar.");
        timeTab.EmitSignal(Button.SignalName.Pressed);

        var initial = session[0].Epoch;
        Require(initial == new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb), "The clock must start at J2000.");
        var utcDate = (LineEdit)scene.FindChild("UtcDate", recursive: true, owned: false);
        var utcTime = (LineEdit)scene.FindChild("UtcTime", recursive: true, owned: false);
        utcDate.Text = "2016-12-31";
        utcTime.Text = "23:59:60";
        utcTime.EmitSignal(LineEdit.SignalName.TextSubmitted, utcTime.Text);
        Require(TimeConversion.ToUtc(session[0].Epoch) is { Second: 60 }, "The time panel must accept a published leap second.");
        scene.Seek(0, initial);

        var renderer = scene.Renderer(0);
        Vector3 initialPosition = renderer.EarthPosition;
        Require(initialPosition.Length() is >= 140 and <= 160, "The solar view must show Earth about one AU from the Sun.");
        scene.Advance(0, JulianInstant.SecondsPerDay);
        Require(session[0].Epoch == initial.AddSeconds(JulianInstant.SecondsPerDay) &&
                renderer.EarthPosition.DistanceTo(initialPosition) >= 1, "Advancing the clock must move Earth.");

        scene.SetMode(0, CameraMode.Lunar);
        Require(renderer.MoonVisible && renderer.EarthPosition == Vector3.Zero &&
                renderer.MoonPosition.Length() is >= 85 and <= 105, "The lunar view must show the Moon around Earth.");
        GD.Print("SpaceTangent DE440 scene smoke passed");
    }

    private static async Task CheckSatellitesAndReload(SolarSystemScene scene)
    {
        var jasonId = new ObjectId("norad/26997");
        var sorceId = new ObjectId("norad/27651");
        SimulationSession session = scene.Session ?? throw new InvalidOperationException("DE440 failed to load.");
        var query = (LineEdit)scene.FindChild("ObjectQuery", recursive: true, owned: false);
        JulianInstant beforeSearch = session[0].Epoch;
        query.Text = "26997";
        query.EmitSignal(LineEdit.SignalName.TextChanged, query.Text);
        query.EmitSignal(LineEdit.SignalName.TextSubmitted, query.Text);
        Require(session[0].Selection == jasonId && session[0].Epoch == beforeSearch,
            "Searching for Jason-1 by NORAD ID must preserve the active epoch.");
        scene.SetMode(0, CameraMode.Satellites);
        var card = (Control)scene.FindChild("ObjectCard", recursive: true, owned: false);
        Require(card.IsVisibleInTree(), "Selecting Jason-1 must open the spacecraft card.");
        var closeCard = (Button)card.FindChild("CloseObjectCard", recursive: true, owned: false);
        closeCard.EmitSignal(Button.SignalName.Pressed);
        Require(!card.IsVisibleInTree(), "The card close control must hide the card.");
        query.EmitSignal(LineEdit.SignalName.TextSubmitted, query.Text);
        Require(card.IsVisibleInTree(), "Selecting the same object must reveal the card again.");
        VerifyMesh(scene.Renderer(0).SatelliteMesh(jasonId), 83_233, 28, 2);
        VerifyMesh(scene.Renderer(0).SatelliteMesh(sorceId), 115_711, 20, 1);

        scene.ToggleSplit();
        Require(session.ViewCount == 2 && session[0] == session[1], "Splitting must copy view state.");
        scene.Synchronize(false, 0);
        scene.Advance(1, 3600);
        Require(session[0].Epoch != session[1].Epoch, "Independent clocks must diverge.");
        Require(scene.Renderer(1).SatelliteMesh(jasonId).Position
                    .DistanceTo(scene.Renderer(0).SatelliteMesh(jasonId).Position) > 5, "Two epochs must move Jason-1 independently.");

        string? oldSettings = System.Environment.GetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH");
        string settings = Path.Combine(Path.GetTempPath(), $"SpaceTangent-smoke-{Guid.NewGuid():N}.txt");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", settings);
        try
        {
            scene.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
            ObserverView left = session[0], right = session[1];
            Require(await scene.ReloadAddOns(), "Real add-ons must reload.");
            Require(left == session[0] && right == session[1] && session.DeltaT == DeltaTModel.MorrisonStephenson2004,
                "Reload must retain both clocks, selections, camera modes and ΔT.");
            Require(File.ReadAllText(settings) == nameof(DeltaTModel.MorrisonStephenson2004), "ΔT must be saved to the profile.");
            VerifyMesh(scene.Renderer(1).SatelliteMesh(jasonId), 83_233, 28, 2);
            await CheckFailedReload(scene, left, right);
            Task<bool> superseded = scene.ReloadAddOns();
            Task<bool> latest = scene.ReloadAddOns();
            bool firstPublished = await superseded;
            bool latestPublished = await latest;
            Require(!firstPublished && latestPublished && left == session[0] && right == session[1],
                "A superseded load must not replace the latest world generation.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SETTINGS_PATH", oldSettings);
            File.Delete(settings);
        }

        var beforeReload = session[1].Epoch;
        session.TogglePlaying(1);
        bool loadedWhilePlaying = await scene.ReloadAddOns();
        session.TogglePlaying(1);
        Require(loadedWhilePlaying && session[1].Epoch.CompareTo(beforeReload) > 0,
            "The second view's clock must keep advancing while add-on models decode.");

        scene.Synchronize(true, 1);
        Require(session[0].Epoch == session[1].Epoch, "Synchronizing must use the chosen view's epoch.");
        JulianInstant synchronized = session[0].Epoch;
        scene.Advance(0, 3600);
        Require(session[0].Epoch == synchronized.AddSeconds(3600) && session[1].Epoch == session[0].Epoch,
            "A synchronized step must move both clocks.");

        GD.Print("SpaceTangent Jason-1 and SORCE two-view reload smoke passed");
    }

    private static async Task CheckFailedReload(SolarSystemScene scene, ObserverView left, ObserverView right)
    {
        string? original = System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT");
        System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        SimulationSession session = scene.Session!;
        SimulationWorld world = session.World;
        try
        {
            bool published = await scene.ReloadAddOns();
            Require(!published && ReferenceEquals(world, session.World) && left == session[0] && right == session[1],
                "A failed reload must leave the published world and views intact.");
            Require(scene.LastAddonDiagnostic is { Code: AddonIssue.MissingResource, Stage: AddonStage.Catalog,
                    Source: "Science satellites" } catalog && Path.GetFileName(catalog.File) == "sciencesat.json",
                "Missing catalogue diagnostics must identify their input.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", original);
        }

        DirectoryInfo copy = Directory.CreateTempSubdirectory("space-tangent-science-diagnostic-");
        try
        {
            string? missing = null;
            foreach (string file in Directory.EnumerateFiles(original!, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(copy.FullName, Path.GetRelativePath(original!, file));
                if (Path.GetFileName(file) == "jas_brsh.png")
                {
                    missing = target;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Require(missing is not null, "The real Jason-1 texture must exist in the original input.");
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", copy.FullName);
            Require(!await scene.ReloadAddOns() && ReferenceEquals(world, session.World) &&
                scene.LastAddonDiagnostic is { Code: AddonIssue.MissingResource, Stage: AddonStage.Texture,
                    Source: "Science satellites" } issue && issue.File == missing,
                "An absent PNG must report the actual texture and keep the published world.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT", original);
            copy.Delete(recursive: true);
        }
    }

    private async Task CheckScenarios(SolarSystemScene scene)
    {
        var jason = new ObjectId("norad/26997");
        SimulationSession session = scene.Session!;
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Satellites);
        scene.SelectSatellite(0, jason);
        scene.Seek(0, session[0].Epoch.AddNanoseconds(137));
        scene.ShowFullMoon(1);
        session.SetDeltaT(DeltaTModel.MorrisonStephenson2004);
        session.TogglePlaying(0);
        SimulationScenario expected = session.CaptureScenario(1);

        string root = Directory.CreateTempSubdirectory("SpaceTangent-scenarios-").FullName;
        string path = Path.Combine(root, "jason-moon.stscene"), invalid = Path.Combine(root, "invalid.stscene");
        try
        {
            scene.GetNode<FileDialog>("SaveScenario").EmitSignal(FileDialog.SignalName.FileSelected, path);
            Require(File.Exists(path), "The scenario save dialog must write a file.");
            scene.ToggleSplit();
            scene.SetMode(0, CameraMode.Solar);
            session.SetDeltaT(DeltaTModel.FixedOffset);
            scene.GetNode<FileDialog>("OpenScenario").EmitSignal(FileDialog.SignalName.FileSelected, path);
            Require(session.ViewCount == 2 && session[0] == expected.Views[0] && session[1] == expected.Views[1] &&
                !session.Synchronized && session.DeltaT == expected.DeltaT && scene.Renderer(0).SatelliteMesh(jason).Visible,
                "The scenario must recreate both renderers and preserve exact epochs, ΔT, playback and selection.");
            scene.GetNode<FileDialog>("SaveScenario").EmitSignal(FileDialog.SignalName.FileSelected, path);
            Require(ScenarioFile.Load(path).ActiveView == expected.ActiveView, "Scenario reopen must restore the active view.");

            ScenarioFile.Save(invalid, expected with { Synchronized = true });
            Require(!scene.LoadScenario(invalid) && session[0] == expected.Views[0] && session[1] == expected.Views[1] &&
                !session.Synchronized, "A scenario with conflicting shared clocks must leave both views unchanged.");
            var missing = new ObjectId("norad/0");
            ScenarioFile.Save(invalid, expected with { Views = [expected.Views[0], expected.Views[1] with { Selection = missing }] });
            Require(!scene.LoadScenario(invalid) && session[0] == expected.Views[0] && session[1] == expected.Views[1],
                "A scenario with a missing object must leave the published views intact.");
            File.WriteAllText(invalid, """{"version":1,"views":[null]}""");
            Require(!scene.LoadScenario(invalid) && session.ViewCount == 2 && session.DeltaT == expected.DeltaT,
                "A malformed scenario must leave the current state intact.");

            scene.Synchronize(true, 1);
            Require(scene.SaveScenario(path), "The synchronized two-view scenario must save.");
            scene.ToggleSplit();
            Require(scene.SaveScenario(invalid), "The one-view scenario must save.");
            Require(scene.LoadScenario(path) && session.ViewCount == 2 && session.Synchronized &&
                session[0].Epoch == session[1].Epoch && session.CaptureScenario(0).ClockOwner == 1,
                "The saved shared clock must retain its owner and epoch.");
            Require(scene.LoadScenario(invalid) && session.ViewCount == 1 && session.Synchronized,
                "Opening a one-view scenario must remove the second renderer.");
            ScenarioFile.Save(path, expected);
            Require(scene.LoadScenario(path), "The original two-view scenario must still open.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(invalid);
            Directory.Delete(root);
        }
        session.TogglePlaying(0);
        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_SCENARIO_CAPTURE") is { Length: > 0 } capture)
        {
            scene.Seek(0, session[0].Epoch);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using Image image = GetViewport().GetTexture().GetImage();
            Require(image.SavePng(capture) == Error.Ok, "Could not save the restored Jason-1 and full-Moon views.");
        }
        GD.Print("SpaceTangent Jason-1 and Full Moon scenario save and restore smoke passed");
    }

    private async Task CheckMission(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session!;
        Require(session.World.MissionId == new ObjectId("naif/-82"), "The original mission catalogue must publish Cassini.");
        if (session.ViewCount == 1)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        ObserverView other = session[1];
        scene.SetMode(0, CameraMode.Mission);
        var renderer = scene.Renderer(0);
        MeshInstance3D cassini = renderer.MissionMesh;
        Require(session[0].Epoch == SimulationSession.MissionPreview && session[1] == other,
            "Mission preview must move only the independent observer.");
        Require(cassini.Visible && cassini.Mesh is ArrayMesh mesh && mesh.GetSurfaceCount() == 10 &&
            Enumerable.Range(0, 10).Sum(mesh.SurfaceGetArrayLen) == 15_750 * 3 &&
            Enumerable.Range(0, 10).Count(index => mesh.SurfaceGetMaterial(index) is StandardMaterial3D material &&
                material.AlbedoColor.A == 0) == 1, "Expected the original 3DS geometry and transparency.");
        // CSPICE position relative to the Saturn barycenter and CK frame X, in J2000.
        Vector3 expected = new(73711.66990445752f, 12783.15305085113f, 89824.80357842673f);
        Vector3 expectedAxis = new(.0731622600765734f, .9970840780335287f, -.021693893899345944f);
        Require(cassini.Position.DistanceTo(expected / 2500) < .0001f &&
            cassini.Basis.X.Normalized().DistanceTo(expectedAxis) < .000001f, "Cassini transform must match SPICE.");
        MeshInstance3D frusta = renderer.MissionFrusta;
        string catalog = System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_CATALOG")!;
        string directory = Path.GetDirectoryName(Path.GetFullPath(catalog))!;
        bool json = string.Equals(Path.GetExtension(catalog), ".json", StringComparison.OrdinalIgnoreCase);
        bool project = json && File.Exists(Path.Combine(directory, "sat480.bsp")) &&
            File.Exists(Path.Combine(directory, "pck00011.tpc"));
        Require(frusta.Visible == (json && !project) && renderer.MissionProjectionVisible == project &&
            !renderer.MissionFootprintVisible, "The preview epoch must show the camera rays without a Saturn intercept.");
        if (json)
        {
            var sensorMesh = (ArrayMesh)frusta.Mesh;
            Require(sensorMesh.GetSurfaceCount() == 2 && sensorMesh.SurfaceGetArrayLen(0) == 16 &&
                sensorMesh.SurfaceGetArrayLen(1) == 16, "Expected two camera frusta with four corners each.");
            var vertices = sensorMesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3 referenceCorner = new(-.992803749673713f, .07445288682761675f, -.09379489472700221f);
            bool match = project ? renderer.MissionProjectedFrusta.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex]
                .AsVector3Array().Any(vertex => vertex.DistanceTo(cassini.Position + referenceCorner * 80) < .0001f)
                : frusta.Position.DistanceTo(cassini.Position) < .00001f &&
                    vertices.Any(vertex => (frusta.Basis * vertex).DistanceTo(referenceCorner * 80) < .0001f);
            Require(match,
                "The rendered camera corner must match CSPICE at the catalogue range.");
        }

        string? capture = System.Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using Image image = renderer.GetViewport().GetTexture().GetImage();
            Require(image.SavePng(capture) == Error.Ok, "Could not save the mission view.");
        }

        scene.SetMode(1, CameraMode.Mission);
        scene.Seek(1, JulianInstant.FromTdbSeconds(141918830.41202343));
        MeshInstance3D second = scene.Renderer(1).MissionMesh;
        Require(second.Visible && second.Position.DistanceTo(cassini.Position) > 10 &&
            second.Basis.X.Normalized().DistanceTo(cassini.Basis.X.Normalized()) > .02f,
            "Separate epochs must change both the mission state and pointing.");
        scene.Seek(1, JulianInstant.FromTdbSeconds(141919293.40898937));
        Require(!second.Visible && cassini.Visible && !scene.Renderer(1).MissionFrusta.Visible &&
            !scene.Renderer(1).MissionProjectionVisible && frusta.Visible == (json && !project),
            "A CK gap must hide only that observer's model and frusta.");
        ObserverView left = session[0], right = session[1];
        Require(await scene.ReloadAddOns() && left == session[0] && right == session[1] && cassini.Visible && !second.Visible,
            "Mission reload must preserve both clocks and pointing availability.");
        if (project)
        {
            scene.Seek(1, JulianInstant.FromTdbSeconds(141922715.38656482));
            SimulationViewport secondRenderer = scene.Renderer(1);
            Require(!renderer.MissionFootprintVisible && secondRenderer.MissionFootprintVisible &&
                secondRenderer.MissionFootprintCount == 2, "Only the observer at the later epoch must show both footprints.");
            if (!string.IsNullOrWhiteSpace(capture))
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using Image image = secondRenderer.GetViewport().GetTexture().GetImage();
                string footprint = Path.Combine(Path.GetDirectoryName(capture)!, "saturn-footprints.png");
                Require(image.SavePng(footprint) == Error.Ok, "Could not save the camera footprints.");
            }
        }
        GD.Print("SpaceTangent Cassini geometry, SPICE transform and two-view reload smoke passed");
    }

    private async Task CheckLua(SolarSystemScene scene, string original)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)));
        Require(hash == "20F0CCC2781C62D6C216D395049BBC64DCF701C41128079DB93C6909E0402C0B",
            "Expected the original pause.celx fixture.");
        SimulationSession session = scene.Session!;
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Solar);
        session.SetPlaying(0, false);
        LuaSessionScript pause = scene.RunScript(original) ?? throw new InvalidOperationException("Could not open pause.celx.");
        pause.Step(0);
        Require(!pause.Finished && session[0].Playing, "The first upstream pause call must toggle the clock and wait.");
        for (int tick = 0; tick < 20 && !pause.Finished; tick++)
            pause.Step(2);
        Require(pause.Finished && pause.Error is null && session[0].Playing, "The original pause script must finish.");
        session.SetPlaying(0, false);

        string path = Path.Combine(Path.GetTempPath(), $"SpaceTangent-lua-{Guid.NewGuid():N}.lua");
        try
        {
            File.WriteAllText(path, "st.settime(2451545.125)\nst.wait(0.03)\nst.mode('lunar')");
            LuaSessionScript normal = scene.RunScript(path) ?? throw new InvalidOperationException("Lua command script did not load.");
            await WaitForScript(normal);
            Require(normal.Error is null && session[0].Mode == CameraMode.Lunar &&
                session[0].Epoch == JulianInstant.FromJulianDate(2451545.125, TimeScale.Tdb),
                "Lua commands must share the application's time and camera paths.");

            File.WriteAllText(path, "while true do end");
            LuaSessionScript loop = scene.RunScript(path) ?? throw new InvalidOperationException("Lua loop did not load.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(!loop.Finished, "The infinite loop must yield control between frames.");
            await WaitForScript(loop);
            Require(loop.Error?.Contains("instruction quota", StringComparison.Ordinal) == true,
                $"The infinite loop must hit the instruction quota. Actual error: {loop.Error}");

            File.WriteAllText(path, """local s = "x"; while true do s = s .. s end""");
            LuaSessionScript growth = scene.RunScript(path) ?? throw new InvalidOperationException("Lua growth script did not load.");
            await WaitForScript(growth);
            Require(growth.Error?.Contains("allocation quota", StringComparison.Ordinal) == true,
                "Exponential string growth must hit the allocation quota.");

            File.WriteAllText(path, "assert(os == nil and io == nil and require == nil and load == nil)");
            LuaSessionScript access = scene.RunScript(path) ?? throw new InvalidOperationException("Lua access probe did not load.");
            await WaitForScript(access);
            Require(access.Error is null, "Lua must not expose external host APIs.");
        }
        finally
        {
            File.Delete(path);
        }
        GD.Print("SpaceTangent original pause.celx and Lua quotas smoke passed");
    }

    private async Task WaitForScript(LuaSessionScript script)
    {
        var time = System.Diagnostics.Stopwatch.StartNew();
        while (!script.Finished)
        {
            Require(time.Elapsed.TotalSeconds < 15, "Lua script did not finish.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private async Task CheckTimeline(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session!;
        Require(session.World.TimelineId == new ObjectId("ssc/sol/saturn/cassini"), "The original sampled catalogue must load.");
        if (session.ViewCount == 1)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Timeline);
        scene.SetMode(1, CameraMode.Timeline);
        var inputs = scene.FindChildren("TimelineEpoch", "LineEdit", recursive: true, owned: false);
        Require(inputs.Count == 2, "Both timeline views need a date input.");
        ((LineEdit)inputs[0]).EmitSignal(LineEdit.SignalName.TextSubmitted, "2453167.499988426");
        ((LineEdit)inputs[1]).EmitSignal(LineEdit.SignalName.TextSubmitted, "2453167.5");
        SimulationViewport left = scene.Renderer(0), right = scene.Renderer(1);
        Require(left.MissionMesh.Visible && left.MissionUsesSun && !left.MissionUsesSaturn &&
            right.MissionMesh.Visible && right.MissionUsesSaturn && !right.MissionUsesSun,
            "The two real phases must display their own centers simultaneously.");
        Require(left.MissionMesh.Position.Length() is > 0 and <= 75.001f &&
            right.MissionMesh.Position.Length() is > 0 and <= 75.001f &&
            left.MissionMesh.Position.DistanceTo(right.MissionMesh.Position) > 1,
            "Both models must be in view at their own display scale.");

        string? capture = System.Environment.GetEnvironmentVariable("SPACETANGENT_TIMELINE_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using Image image = GetViewport().GetTexture().GetImage();
            Require(image.SavePng(capture) == Error.Ok, "Could not save both Cassini timeline views.");
        }

        ObserverView first = session[0], second = session[1];
        string file = Path.Combine(Path.GetTempPath(), $"SpaceTangent-timeline-{Guid.NewGuid():N}.stscene");
        try
        {
            Require(scene.SaveScenario(file), "The timeline scenario must save.");
            ((LineEdit)inputs[1]).EmitSignal(LineEdit.SignalName.TextSubmitted, "2459000");
            Require(!right.MissionMesh.Visible && left.MissionMesh.Visible, "Only the observer outside the timeline must hide Cassini.");
            Require(scene.LoadScenario(file) && first == session[0] && second == session[1] &&
                left.MissionUsesSun && right.MissionUsesSaturn, "Scenario opening must restore both timeline phases.");
            Require(await scene.ReloadAddOns() && first == session[0] && second == session[1] &&
                left.MissionUsesSun && right.MissionUsesSaturn, "Reload must keep both sampled phases selected.");
        }
        finally
        {
            File.Delete(file);
        }
        GD.Print("SpaceTangent original fifteen-phase Cassini timeline smoke passed");
    }

    private async Task CheckProbe(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session!;
        var probe = session.World.Probe!;
        Require(probe.Id == new ObjectId("ssc/sol/saturn/titan/huygens"), "The original Huygens entry must load.");
        if (session.ViewCount == 1)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Probe);
        scene.SetMode(1, CameraMode.Probe);

        MeshInstance3D left = scene.Renderer(0).MissionMesh, right = scene.Renderer(1).MissionMesh;
        scene.Seek(0, probe.Separation.AddSeconds(-1));
        scene.Seek(1, probe.Landing);
        Require(left.Visible && right.Visible && !left.Basis.X.IsEqualApprox(right.Basis.X),
            "The real mounted and landed probe must use distinct poses.");
        Require(scene.Renderer(0).ProbeStatus?.StartsWith("Attached to Cassini", StringComparison.Ordinal) == true &&
            scene.Renderer(1).ProbeStatus == "Landed on Titan", "Each view must show its own flight phase.");
        var mesh = (ArrayMesh)left.Mesh;
        int vertices = Enumerable.Range(0, mesh.GetSurfaceCount()).Sum(mesh.SurfaceGetArrayLen);
        Require(mesh.GetSurfaceCount() == 4 && vertices == 6098 * 3, "The original Huygens model must retain its faces and materials.");
        scene.Seek(0, probe.Separation);
        Basis first = left.Basis;
        scene.Seek(0, probe.Separation.AddSeconds(1.99998));
        Require(left.Visible && right.Visible && Math.Abs(first.Y.Normalized().Dot(left.Basis.Y.Normalized())) < .001f &&
            first.X.Normalized().Dot(left.Basis.X.Normalized()) > .9999f,
            "The free-flying model must turn a quarter revolution in about two seconds.");
        Require(scene.Renderer(0).ProbeStatus?.StartsWith("Free flight", StringComparison.Ordinal) == true,
            "The phase label must update at separation.");

        string? capture = System.Environment.GetEnvironmentVariable("SPACETANGENT_PROBE_CAPTURE");
        if (!string.IsNullOrWhiteSpace(capture))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using Image image = GetViewport().GetTexture().GetImage();
            Require(image.SavePng(capture) == Error.Ok, "Could not save both Huygens views.");
        }

        ObserverView flying = session[0], landed = session[1];
        string file = Path.Combine(Path.GetTempPath(), $"SpaceTangent-probe-{Guid.NewGuid():N}.stscene");
        try
        {
            Require(scene.SaveScenario(file), "The probe scenario must save.");
            scene.Seek(0, probe.Start.AddSeconds(-1));
            Require(!left.Visible && right.Visible, "Missing probe coverage must only hide the affected view.");
            Require(scene.LoadScenario(file) && flying == session[0] && landed == session[1] && left.Visible && right.Visible,
                "Scenario opening must restore both probe phases.");
            Require(await scene.ReloadAddOns() && flying == session[0] && landed == session[1] && left.Visible && right.Visible,
                "Add-on reload must preserve both probe phases.");
        }
        finally
        {
            File.Delete(file);
        }
        GD.Print("SpaceTangent original three-phase Huygens timeline smoke passed");
    }

    private async Task CheckSscOverlays(SolarSystemScene scene, string root)
    {
        const string Variable = "SPACETANGENT_TIMELINE_ROOT";
        string? previousRoot = System.Environment.GetEnvironmentVariable(Variable);
        SimulationSession session = scene.Session!;
        if (session.ViewCount == 1)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.Timeline);
        scene.Seek(0, JulianInstant.FromJulianDate(2453167.5, TimeScale.Tdb));
        ObserverView left = session[0], right = session[1];
        var deltaT = session.DeltaT;
        MeshInstance3D model = scene.Renderer(0).MissionMesh;
        Vector3 position = model.Position;
        Require(model.Visible && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 10,
            "The unmodified view must show the original Cassini model.");
        try
        {
            System.Environment.SetEnvironmentVariable(Variable, root);
            Require(await scene.ReloadAddOns() && session[0] == left && session[1] == right && session.DeltaT == deltaT &&
                model.Visible && model.Position.IsEqualApprox(position) && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 4,
                "Ordered SSC patches must replace geometry without changing either epoch, the trajectory or ΔT.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(Variable, previousRoot);
        }
        Require(await scene.ReloadAddOns() && model.Visible && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 10,
            "Removing the SSC overrides must restore the original Cassini model.");
        GD.Print("SpaceTangent ordered SSC overlay reload smoke passed");
    }

    private async Task CheckLunarTerrain(SolarSystemScene scene)
    {
        SimulationSession session = scene.Session!;
        Require(session.World.HasLunarTerrain, "The real lunar elevation map and PCK must load.");
        if (session.ViewCount == 1)
            scene.ToggleSplit();
        scene.Synchronize(false, 0);
        scene.SetMode(0, CameraMode.LunarTerrain);
        scene.SetMode(1, CameraMode.LunarTerrain);
        scene.SetLunarTerrainCamera(0, LunarTerrainCamera.Globe);
        scene.SetLunarTerrainCamera(1, LunarTerrainCamera.Copernicus);
        scene.Seek(0, SurfaceProjection.FullMoonEpoch);
        scene.Seek(1, SurfaceProjection.FullMoonEpoch.AddSeconds(-3.8 * JulianInstant.SecondsPerDay));
        LunarTerrainProjection left = scene.Renderer(0).Terrain!, right = scene.Renderer(1).Terrain!;
        Require(left.Visible && right.Visible && !left.Regional && right.Regional &&
            left.SunDirection.DistanceTo(right.SunDirection) > .5f, "Terrain must draw at separate scales and solar epochs.");
        Require(Math.Abs(left.SunDirection.Length() - 1) < 1e-6f && Math.Abs(right.SunDirection.Length() - 1) < 1e-6f,
            "The terrain shader requires unit light directions.");

        if (System.Environment.GetEnvironmentVariable("SPACETANGENT_TERRAIN_CAPTURE") is { Length: > 0 } capture)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using Image image = GetViewport().GetTexture().GetImage();
            Require(image.SavePng(capture) == Error.Ok, "Could not save the globe and regional relief views.");
        }

        ObserverView globe = session[0], crater = session[1];
        string file = Path.Combine(Path.GetTempPath(), $"SpaceTangent-terrain-{Guid.NewGuid():N}.stscene");
        try
        {
            Require(scene.SaveScenario(file), "The lunar terrain scenario must save.");
            scene.SetLunarTerrainCamera(1, LunarTerrainCamera.Globe);
            Require(!right.Regional, "Changing one view must select the new scale.");
            Require(scene.LoadScenario(file) && session[0] == globe && session[1] == crater && right.Regional,
                "Opening a scenario must restore both lunar cameras.");
            Require(await scene.ReloadAddOns() && session[0] == globe && session[1] == crater && left.Visible && right.Visible,
                "Reloading terrain must preserve both observers and cameras.");
            scene.ShowFullMoon(0);
            Require(!left.Visible && right.Visible && session[1] == crater,
                "Returning to Earth must hide only that observer's terrain.");
        }
        finally
        {
            File.Delete(file);
        }
        GD.Print("SpaceTangent real lunar terrain two-view and scenario smoke passed");
    }

    private async Task CheckAddonCache(SolarSystemScene scene, string source)
    {
        const string Variable = "SPACETANGENT_TIMELINE_ROOT";
        string? previous = System.Environment.GetEnvironmentVariable(Variable);
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-addon-cache-");
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(root.FullName, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            System.Environment.SetEnvironmentVariable(Variable, root.FullName);
            Require(await scene.ReloadAddOns(), "The isolated original timeline must load.");
            var session = scene.Session!;
            scene.SetMode(0, CameraMode.Timeline);
            scene.Seek(0, JulianInstant.FromJulianDate(2453167.5, TimeScale.Tdb));
            var renderer = scene.Renderer(0);
            MeshInstance3D satellite = renderer.SatelliteMesh(new ObjectId("norad/26997"));
            MeshInstance3D model = renderer.MissionMesh;
            var originalWorld = session.World;
            Mesh originalModel = model.Mesh;
            File.WriteAllText(Path.Combine(root.FullName, "unused.txt"), "The loader never reads this file.");
            Require(await scene.ReloadAddOns() && ReferenceEquals(originalWorld, session.World) &&
                ReferenceEquals(originalModel, model.Mesh), "Unobserved files must preserve the cached world and model.");

            string baseModels = Path.Combine(root.FullName, "models");
            string probe = Path.Combine(baseModels, "huygens.3ds");
            string carrier = Path.Combine(baseModels, "cassini.3ds");
            string preferred = Path.Combine(root.FullName, "ssc", "cassini.3ds");
            File.Copy(probe, preferred);
            Require(await scene.ReloadAddOns() && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 4,
                "A previously missing preferred mesh must invalidate the cached timeline.");
            File.Delete(preferred);

            DirectoryInfo overlays = root.CreateSubdirectory("overrides");
            string patch = Path.Combine(overlays.FullName, "10-model.ssc");
            File.Copy(probe, Path.Combine(overlays.FullName, "model-a.3ds"));
            File.Copy(carrier, Path.Combine(overlays.FullName, "model-b.3ds"));
            WritePatch("model-a.3ds");
            Require(await scene.ReloadAddOns() && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 4,
                "A newly created override directory must be observed.");
            var epoch = session[0];
            var other = session[1];
            var deltaT = session.DeltaT;
            DateTime timestamp = File.GetLastWriteTimeUtc(patch);
            long bytes = new FileInfo(patch).Length;
            WritePatch("model-b.3ds");
            File.SetLastWriteTimeUtc(patch, timestamp);
            Require(File.GetLastWriteTimeUtc(patch) == timestamp && new FileInfo(patch).Length == bytes,
                "The controlled edit must preserve both the original timestamp and file size.");
            Require(await scene.ReloadAddOns() && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 10 &&
                ReferenceEquals(satellite, renderer.SatelliteMesh(new ObjectId("norad/26997"))) &&
                session[0] == epoch && session[1] == other && session.DeltaT == deltaT,
                "An equal-size SSC edit must refresh only the affected assets and preserve the observer state.");

            var stableWorld = session.World;
            Mesh stableModel = model.Mesh;
            File.WriteAllText(patch, "Modify \"Cassini\" \"Sol/Saturn\" {\n    Mesh }");
            Require(!await scene.ReloadAddOns() && ReferenceEquals(stableWorld, session.World) &&
                scene.LastAddonDiagnostic is { Code: AddonIssue.InvalidInput, Stage: AddonStage.Catalog,
                    Source: "Sampled timeline", Line: 2 } syntax && syntax.File == patch,
                "SSC syntax errors must identify the override and line.");
            WritePatch("model-c.3ds");
            Require(!await scene.ReloadAddOns() && ReferenceEquals(stableWorld, session.World) &&
                ReferenceEquals(stableModel, model.Mesh), "A missing required mesh must leave the published generation intact.");
            Require(scene.LastAddonDiagnostic is { Code: AddonIssue.MissingResource, Stage: AddonStage.Mesh,
                    Source: "Sampled timeline" } absent && absent.File == Path.Combine(overlays.FullName, "model-c.3ds"),
                "A missing mesh must name the actual file selected by the override.");
            File.Copy(probe, Path.Combine(overlays.FullName, "model-c.3ds"));
            Task<bool> superseded = scene.ReloadAddOns();
            Task<bool> latest = scene.ReloadAddOns();
            Require(!await superseded && await latest && ((ArrayMesh)model.Mesh).GetSurfaceCount() == 4 &&
                scene.LastAddonDiagnostic is null,
                "The latest generation must publish the newly available resource.");

            void WritePatch(string mesh) => File.WriteAllText(patch,
                $"Modify \"Cassini\" \"Sol/Saturn\" {{ Mesh \"{mesh}\" }}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(Variable, previous);
            root.Delete(recursive: true);
        }
        Require(await scene.ReloadAddOns(), "Restore the original timeline after the cache test.");
        GD.Print("SpaceTangent exact add-on cache and atomic reload smoke passed");
        GD.Print("SpaceTangent import diagnostics smoke passed");
    }

    private static void VerifyMesh(MeshInstance3D node, int triangles, int surfaces, int textureCount)
    {
        if (!node.Visible || node.Mesh is not ArrayMesh mesh || node.Position.Length() is < 85 or > 120)
            throw new InvalidOperationException("Expected a real mesh in Earth orbit.");
        Require(mesh.GetSurfaceCount() == surfaces, "Unexpected number of material surfaces.");

        int vertices = 0;
        var textures = new HashSet<ulong>();
        for (int i = 0; i < surfaces; i++)
        {
            vertices += mesh.SurfaceGetArrayLen(i);
            if (mesh.SurfaceGetMaterial(i) is not StandardMaterial3D material ||
                material.Transparency != BaseMaterial3D.TransparencyEnum.Disabled)
                throw new InvalidOperationException("Unexpected transparent material.");
            if (material.AlbedoTexture is { } texture)
                textures.Add(texture.GetInstanceId());
        }
        Require(vertices == triangles * 3 && textures.Count == textureCount, "Unexpected geometry or texture count.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
