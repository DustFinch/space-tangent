// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

/// <summary>Runs real startup arguments against an isolated preference sentinel, including exit-time saves.</summary>
public partial class ObserverPlaceStartupProbe : Node
{
    private SolarSystemScene? _scene;
    private byte[]? _before;
    private string _path = "";
    private bool _normal, _edited, _failed;
    private double _wait;
    private static readonly EarthObserverCamera Home = EarthObserverCamera.Default with { Site = new(-33.9, 18.4, .02) };

    public override void _Ready()
    {
        try
        {
            string directory = System.Environment.GetEnvironmentVariable("SPACETANGENT_EARTH_PROBE_OUTPUT")
                ?? throw new InvalidOperationException("Set an isolated startup probe directory.");
            Directory.CreateDirectory(directory);
            _normal = OS.GetCmdlineUserArgs().Length == 0;
            _path = Path.Combine(directory, "sentinel-places.json");
            System.Environment.SetEnvironmentVariable("SPACETANGENT_OBSERVER_PLACES_PATH", _path);
            var session = new SimulationSession(new(De440Ephemeris.Load(
                System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!), []),
                SurfaceProjection.FullMoonEpoch, DeltaTModel.FixedOffset);
            session.SetEarthObserver(0, Home);
            ObserverPlacePreferences.Save(_path, session);
            _before = File.ReadAllBytes(_path);
            File.WriteAllBytes(_path + ".before", _before);
            // Avoid playback-rate normalization of synchronized, rate-less imported views in the exact-state smoke.
            session.Synchronize(false, 0);
            session.SetEarthObserver(0, EarthObserverCamera.GreenwichFullMoon);
            var scenario = session.CaptureScenario(0);
            ScenarioFile.Save(Path.Combine(directory, "startup.stscene"), scenario);
            using (var stream = File.Create(Path.Combine(directory, "startup.stframes")))
            using (var writer = new ObserverFrameSequence.Writer(stream, 30))
            {
                writer.Append(scenario);
                writer.Complete();
            }
            if (OS.GetCmdlineUserArgs().Contains("--smoke-startup"))
                System.Environment.SetEnvironmentVariable("SPACETANGENT_SMOKE_SCENARIO", Path.Combine(directory, "startup.stscene"));
            _scene = GD.Load<PackedScene>("res://Main.tscn").Instantiate<SolarSystemScene>();
            AddChild(_scene);
        }
        catch (Exception error) { Fail(error); }
    }

    public override void _Process(double delta)
    {
        if (_failed || _scene?.InitializationFinished != true) return;
        try
        {
            var session = _scene.Session ?? throw new InvalidOperationException("Startup requires a live simulation.");
            if (_scene.Replaying) return;
            if (!_edited)
            {
                bool defaultBatch = OS.GetCmdlineUserArgs().Contains("--startup-default-settings");
                if ((_normal && session[0].ObserverCamera != Home) || (defaultBatch && session[0].ObserverCamera == Home))
                    throw new InvalidDataException("Startup did not honor its preference read policy.");
                session.SetEarthObserver(0, EarthObserverCamera.Tromso);
                _edited = true;
            }
            if ((_wait += delta) < 1) return;
            // Removing the real scene exercises its exit flush after the debounce interval.
            RemoveChild(_scene);
            _scene.QueueFree();
            _scene = null;
            GetTree().Quit();
        }
        catch (Exception error) { Fail(error); }
    }

    private void Fail(Exception error)
    {
        _failed = true;
        GD.PushError(error.ToString());
        GetTree().Quit(1);
    }

    public override void _ExitTree()
    {
        try
        {
            if (_failed || _before is null) return;
            bool unchanged = _before.SequenceEqual(File.ReadAllBytes(_path));
            if (unchanged == _normal)
                throw new InvalidDataException("Startup did not honor its preference write policy.");
            if (_normal && !_edited)
                throw new InvalidDataException("The normal control did not exercise a preference edit.");
            string[] arguments = OS.GetCmdlineUserArgs();
            int saveIndex = Array.IndexOf(arguments, "--startup-save-scenario");
            if (saveIndex >= 0)
            {
                if (saveIndex + 1 >= arguments.Length) throw new InvalidDataException("Missing scenario output path.");
                _ = ScenarioFile.Load(arguments[saveIndex + 1]);
            }
            if (CoverageCapture.Complete(0) != 0)
                throw new InvalidOperationException("Startup coverage could not be flushed.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(_path)!, "policy-result.json"),
                System.Text.Json.JsonSerializer.Serialize(new { passed = true, normal = _normal, edited = _edited }));
            GD.Print("SpaceTangent observing place startup policy passed: " + (_normal ? "normal" : "batch"));
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            // The external suite also requires a success result and unchanged sentinel
            // after native exit: a host ignoring a late Quit(1) cannot hide a failure.
            System.Environment.ExitCode = 1;
            GetTree().Quit(1);
        }
    }
}
