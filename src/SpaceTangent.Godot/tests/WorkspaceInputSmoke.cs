// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke : Node
{
    private static readonly ObjectId Jason = new("norad/26997");
    private SolarSystemScene _scene = null!;
    private SimulationSession Session => _scene.Session!;
    private SceneTree Tree => GetTree();

    public override async void _Ready()
    {
        const string settingsVariable = "SPACETANGENT_SETTINGS_PATH";
        const string scienceVariable = "SPACETANGENT_SCIENCE_ROOT";
        string? oldSettings = System.Environment.GetEnvironmentVariable(settingsVariable);
        string? science = System.Environment.GetEnvironmentVariable(scienceVariable);
        int exitCode = 1;
        DirectoryInfo? scratch = null;
        try
        {
            Require(!string.IsNullOrWhiteSpace(science) && Directory.Exists(science),
                "Set SPACETANGENT_SCIENCE_ROOT to the original science add-ons.");
            scratch = Directory.CreateTempSubdirectory("SpaceTangent-ui-");
            string settings = Path.Combine(scratch.FullName, "delta-t.txt");
            System.Environment.SetEnvironmentVariable(settingsVariable, settings);

            Tree.Root.Size = new(1600, 900);
            Tree.Root.GuiEmbedSubwindows = true;
            _scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(_scene);
            await Until(() => _scene.InitializationFinished, "Simulation startup", 25);
            Require(_scene.Session is not null && Session.World.Satellites.Any(satellite => satellite.Id == Jason),
                "DE440 and the original Jason-1 add-on must load.");
            Tree.Root.NotifyMouseEntered();

            await ExerciseViews();
            var expected = Session.CaptureScenario(1);
            Require(expected.ActiveView == 1 && !expected.Synchronized &&
                    expected.Views[0].Navigation is { Tracking: true } &&
                    expected.Views[1].Mode == CameraMode.Surface,
                "The capture must have Jason-1 tracking and an independent full-Moon view.");
            var copiedScience = Path.Combine(scratch.FullName, "science");
            foreach (string file in Directory.EnumerateFiles(science!, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(copiedScience, Path.GetRelativePath(science!, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var oldWorld = Session.World;
            System.Environment.SetEnvironmentVariable(scienceVariable, copiedScience);
            await Click(Find<Button>("AddOnsTab"));
            await Click(Find<Button>("ReloadAddOns"));
            await Until(() => !ReferenceEquals(oldWorld, Session.World), "Republishing the add-on world", 20);
            Require(Find<Label>("WorkspaceStatus").Text == "Add-ons reloaded.",
                "The reload control must report success.");
            RequireScenario(expected, Session.CaptureScenario(1), "Reloading add-ons");
            Require(File.ReadAllText(settings).Trim() == nameof(DeltaTModel.MorrisonStephenson2004),
                "The ΔT selection must persist to disk after reload.");
            GD.Print("UI: reloaded original add-ons and preserved both view states and ΔT");

            const string filename = "jason-and-moon.stscene";
            string scenario = Path.Combine(scratch.FullName, filename);
            await UseScenarioDialog("SaveScenario", 1, scratch.FullName, filename);
            await Until(() => File.Exists(scenario), "Saving through the file dialog");
            RequireScenario(expected, ScenarioFile.Load(scenario), "Saving through the file dialog");
            await Click(Find<Button>("SplitViews"));
            Require(Session.ViewCount == 1, "Closing the second view must change the current state.");
            await ChooseDeltaT(DeltaTModel.EspenakMeeus2006);
            await UseScenarioDialog("OpenScenario", 0, scratch.FullName, filename);
            await Until(() => Find<Label>("WorkspaceStatus").Text == "Scenario opened.", "Opening the saved scenario");
            RequireScenario(expected, Session.CaptureScenario(1), "Reopening the saved scenario");
            Require(_scene.Renderer(0).NavigationCameraCurrent && _scene.Renderer(0).SatelliteMesh(Jason).Visible,
                "The tracked Jason-1 camera and mesh must be restored.");
            Require(Find<OptionButton>("DeltaTModel").GetSelectedId() == (int)expected.DeltaT,
                "The ΔT control must reflect the restored scenario.");
            GD.Print("UI: saved, changed and reopened the scenario with both cameras and ΔT restored");
            GD.Print($"SpaceTangent workspace input smoke passed ({DisplayServer.GetName()})");
            exitCode = 0;
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(settingsVariable, oldSettings);
            System.Environment.SetEnvironmentVariable(scienceVariable, science);
            scratch?.Delete(recursive: true);
            Tree.Quit(exitCode);
        }
    }

    private async Task ExerciseViews()
    {
        await KeyPress(Key.U, ctrl: true);
        Require(Session.ViewCount == 2, "Ctrl+U must create two views.");
        await Click(Find<Button>("TimeTab"));
        await Click(Find<CheckButton>("SyncViewTime"));
        Require(!Session.Synchronized, "The time controls must allow independent clocks.");

        await Click(Find<Button>("SearchTab"));
        var query = Find<LineEdit>("ObjectQuery");
        await Click(query);
        Require(query.HasFocus(), "Clicking the search field must give it internal GUI focus.");
        await TypeText("26997");
        Require(query.Text == "26997", "The search field must receive the typed catalogue ID.");
        var results = Find<Container>("ObjectResults");
        var jason = results.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>()
            .Single(button => button.TooltipText == Jason.Value);
        await Click(jason);
        Require(Session[0].Selection == Jason, "Clicking the filtered result must select Jason-1 in the left view.");
        var goTo = Find<Button>("GoToObject");
        await Click(goTo);
        Require(Session.IsFlying(0), "Clicking Go to must start the flight.");
        await KeyPress(Key.Escape);
        Require(!Session.IsFlying(0) && Find<Label>("WorkspaceStatus").Text == "Flight stopped.",
            "Escape must cancel the flight and report that it stopped.");
        await Click(goTo);
        Require(Session.IsFlying(0), "The flight must be restartable after cancellation.");
        await Until(() => !Session.IsFlying(0), "Flying to Jason-1");
        Require(Session[0].Navigation is { Target: { } target } && target == Jason &&
                _scene.Renderer(0).NavigationCameraCurrent, "The flight must reach Jason-1.");
        await Click(Find<Button>("TrackObject"));
        var tracked = Session[0].Navigation;
        Require(tracked is { Tracking: true }, "Clicking Track must lock the camera onto Jason-1.");

        var right = Find<SubViewportContainer>("ViewCanvas1");
        await KeyPress(Key.Tab);
        await Click(Find<Button>("LocationTab"));
        await Click(Find<Button>("ShowFullMoon"));
        Require(Session[1].Mode == CameraMode.Surface && Session[1].Epoch == SurfaceProjection.FullMoonEpoch,
            "The right view must show the full-Moon preset.");
        var moon = Session[1];
        var epoch = Session[0].Epoch;
        await Click(Find<Button>("AdvanceHour0"));
        Require(Session[0].Epoch == epoch.AddSeconds(3600) && Session[1] == moon &&
                Session[0].Navigation is { Tracking: true } moved && moved.AnchorKilometers != tracked!.Value.AnchorKilometers,
            "The left hour step must track Jason-1 while preserving the right camera and time.");
        GD.Print("UI: split and switched by shortcut, canceled and completed the flight to Jason-1, and tracked it in an independent view");

        await Click(Find<Button>("TimeTab"));
        var sync = Find<CheckButton>("SyncViewTime");
        await Click(sync);
        Require(Session.Synchronized && Session[1].Epoch == Session[0].Epoch, "The sync toggle must join both clocks.");
        epoch = Session[0].Epoch;
        await Click(Find<Button>("AdvanceHour0"));
        Require(Session[0].Epoch == epoch.AddSeconds(3600) && Session[1].Epoch == Session[0].Epoch,
            "A step with sync enabled must advance both views.");
        await Click(sync);
        await Click(right);
        await Click(Find<Button>("LocationTab"));
        await Click(Find<Button>("ShowFullMoon"));
        Require(!Session.Synchronized && Session[0].Epoch != Session[1].Epoch &&
                Session[1].Epoch == SurfaceProjection.FullMoonEpoch, "Disabling sync must allow restoring the full Moon independently.");
        await ChooseDeltaT(DeltaTModel.MorrisonStephenson2004);
        GD.Print("UI: synchronized both views, restored independent epochs and selected Morrison–Stephenson ΔT");
    }

    private async Task ChooseDeltaT(DeltaTModel model)
    {
        var tab = Find<Button>("TimeTab");
        if (!tab.ButtonPressed)
            await Click(tab);
        var option = Find<OptionButton>("DeltaTModel");
        await Click(option);
        await ChooseMenuItem(option.GetPopup(), option.GetItemIndex((int)model));
        Require(Session.DeltaT == model && option.GetSelectedId() == (int)model, "The ΔT menu must apply the selected model.");
    }

    private async Task UseScenarioDialog(string dialogName, int menuIndex, string directory, string filename)
    {
        var tab = Find<Button>("BookmarksTab");
        if (!tab.ButtonPressed)
            await Click(tab);
        var menu = Find<MenuButton>("ScenarioMenu");
        var dialog = _scene.GetNode<FileDialog>(dialogName);
        // Supply the fixture directory without depending on a machine's default path.
        dialog.CurrentDir = directory;
        await Click(menu);
        await ChooseMenuItem(menu.GetPopup(), menuIndex);
        await Until(() => dialog.Visible, $"Opening {dialogName}");
        var name = dialog.GetLineEdit();
        await Click(name);
        Require(name.HasFocus(), $"The {dialogName} filename field must receive focus.");
        await KeyPress(Key.A, ctrl: true);
        await TypeText(filename);
        Require(name.Text == filename, $"The {dialogName} filename field must receive the full filename.");
        await KeyPress(Key.Enter);
    }

    private async Task ChooseMenuItem(PopupMenu menu, int index)
    {
        Require(menu.Visible && index >= 0, "The requested popup item must be visible.");
        for (int attempt = 0; menu.GetFocusedItem() != index && attempt < menu.ItemCount; attempt++)
            await KeyPress(Key.Down);
        Require(menu.GetFocusedItem() == index, $"Keyboard navigation did not reach popup item {index}.");
        await KeyPress(Key.Enter);
    }

    private T Find<T>(string name) where T : Node =>
        _scene.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name} {name}.");

    private async Task Click(Control control)
    {
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        Require(control.IsVisibleInTree() && control.Size.X > 0 && control.Size.Y > 0,
            $"Cannot click hidden or empty control {control.Name}.");
        Window root = Tree.Root;
        Vector2 point = root.GetScreenTransform().AffineInverse() *
            (control.GetScreenTransform() * (control.Size / 2));
        using var move = new InputEventMouseMotion { Position = point, GlobalPosition = point };
        root.PushInput(move, inLocalCoords: true);
        using var down = MouseButtonAt(point, true);
        root.PushInput(down, inLocalCoords: true);
        using var up = MouseButtonAt(point, false);
        root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private static InputEventMouseButton MouseButtonAt(Vector2 point, bool down) => new()
    {
        Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left,
        ButtonMask = down ? MouseButtonMask.Left : 0, Pressed = down
    };

    private async Task TypeText(string text)
    {
        foreach (char character in text)
            await KeyPress((Key)char.ToUpperInvariant(character), character);
    }

    private async Task KeyPress(Key key, uint unicode = 0, bool ctrl = false)
    {
        using var down = new InputEventKey { Keycode = key, Unicode = unicode, CtrlPressed = ctrl, Pressed = true };
        Tree.Root.PushInput(down, inLocalCoords: true);
        using var up = new InputEventKey { Keycode = key, CtrlPressed = ctrl };
        Tree.Root.PushInput(up, inLocalCoords: true);
        await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
    }

    private async Task Until(Func<bool> ready, string description, int timeoutSeconds = 6)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            Require(stopwatch.Elapsed.TotalSeconds < timeoutSeconds, $"{description} timed out.");
            await ToSignal(Tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void RequireScenario(SimulationScenario expected, SimulationScenario actual, string action) =>
        Require(expected.Views.SequenceEqual(actual.Views) && expected.DeltaT == actual.DeltaT &&
                expected.Synchronized == actual.Synchronized && expected.ClockOwner == actual.ClockOwner &&
                expected.ActiveView == actual.ActiveView,
            $"{action} must preserve both cameras, epochs, selections, sync settings and ΔT.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
