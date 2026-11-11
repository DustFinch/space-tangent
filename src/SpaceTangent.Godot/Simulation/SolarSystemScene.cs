// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Node3D
{
    private const double OverviewKilometersPerUnit = 1_000_000;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SatelliteKilometersPerUnit = 75;
    private const double SimulatedSecondsPerSecond = JulianInstant.SecondsPerDay;
    private enum View { Solar, Lunar, Satellites }
    private sealed record SatelliteDisplay(ScienceSatellite Track, MeshInstance3D Node, float MarkerScale);

    private readonly List<SatelliteDisplay> _satellites = [];
    private De440Ephemeris? _ephemeris;
    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!;
    private Label _status = null!;
    private Button _play = null!, _scale = null!;
    private bool _playing;
    private View _view;

    internal SolarSystemSample? CurrentSample { get; private set; }

    public override void _Ready()
    {
        _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Position = new(0, 400, 0) };
        AddChild(_camera);
        _camera.LookAt(Vector3.Zero, Vector3.Forward);
        _camera.MakeCurrent();

        _sun = CreateMarker("Sun", new Color("#ffce69"), 4);
        _earth = CreateMarker("Earth", new Color("#6fa8ff"), 3);
        _moon = CreateMarker("Moon", new Color("#cbd3e4"), 2.4f);
        VBoxContainer controls = CreateControls();
        _status = new Label();
        controls.AddChild(_status);

        if (!TryOpenEphemeris(FindEphemerisPath()))
            return;

        var buttons = new HBoxContainer();
        controls.AddChild(buttons);
        AddButton(buttons, "−1 day", () => Advance(-JulianInstant.SecondsPerDay));
        _play = AddButton(buttons, "Play", ToggleClock);
        AddButton(buttons, "+1 day", () => Advance(JulianInstant.SecondsPerDay));
        _scale = AddButton(buttons, "Earth–Moon view", () => SetLunarView(_view != View.Lunar));
        LoadSatellites(controls, buttons);
        Seek(new(JulianInstant.J2000Day, 0, TimeScale.Tdb));
    }

    public override void _Process(double delta)
    {
        if (_playing)
            Advance(delta * (_view == View.Satellites ? 180 : SimulatedSecondsPerSecond));
    }

    internal void Seek(JulianInstant epoch)
    {
        De440Ephemeris model = _ephemeris ?? throw new InvalidOperationException("Load DE440 before setting the clock.");
        CurrentSample = model.Sample(epoch);
        DrawSample();
    }

    internal void SetLunarView(bool enabled)
    {
        SetView(enabled ? View.Lunar : View.Solar);
    }

    internal void SetSatelliteView()
    {
        if (_satellites.Count == 0)
            throw new InvalidOperationException("Load a satellite catalogue before opening its view.");

        SetView(View.Satellites);
        if (CurrentSample is { } current && !_satellites[0].Track.TrySample(current.Epoch, out _))
            Seek(_satellites[0].Track.Epoch);
    }

    private void SetView(View view)
    {
        _view = view;
        _scale.Text = view == View.Lunar ? "Solar overview" : "Earth–Moon view";
        DrawSample();
    }

    internal void Advance(double simulatedSeconds)
    {
        if (CurrentSample is not { } sample || _ephemeris is not { } model)
            return;

        JulianInstant epoch = sample.Epoch.AddSeconds(simulatedSeconds);
        if (epoch.CompareTo(model.Start) <= 0)
        {
            epoch = model.Start;
            SetPlaying(false);
        }
        else if (epoch.CompareTo(model.End) >= 0)
        {
            epoch = model.End;
            SetPlaying(false);
        }

        Seek(epoch);
    }

    private static string FindEphemerisPath()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_DE440");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        string local = ProjectSettings.GlobalizePath("res://.local-data/de440.440");
        return File.Exists(local) ? local : ProjectSettings.GlobalizePath("user://de440.440");
    }

    private bool TryOpenEphemeris(string path)
    {
        try
        {
            _ephemeris = De440Ephemeris.Load(path);
            return true;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            _status.Text = "Set SPACETANGENT_DE440 to a classic JPL DE440 file to start the simulation.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
        }

        _sun.Hide();
        _earth.Hide();
        _moon.Hide();
        return false;
    }

    private void DrawSample()
    {
        if (CurrentSample is not { } sample)
            return;

        ObjectId origin = _view == View.Solar ? SolarSystemSample.SunId : SolarSystemSample.EarthId;
        double scale = _view == View.Solar ? OverviewKilometersPerUnit : LunarKilometersPerUnit;
        _sun.Position = ToGodot(sample.RelativeState(SolarSystemSample.SunId, origin).PositionKilometers, scale);
        _earth.Position = ToGodot(sample.RelativeState(SolarSystemSample.EarthId, origin).PositionKilometers, scale);
        _moon.Position = ToGodot(sample.RelativeState(SolarSystemSample.MoonId, origin).PositionKilometers, scale);
        _sun.Visible = _view == View.Solar;
        _moon.Visible = _view == View.Lunar;
        _camera.Size = _view == View.Solar ? 365 : 250;
        DrawSatellites(sample.Epoch);
        string epoch = FormattableString.Invariant($"JD {sample.Epoch.ToJulianDate():F6} TDB");
        _status.Text = _view switch
        {
            View.Solar => $"{epoch}   Solar overview",
            View.Lunar => $"{epoch}   Earth–Moon view",
            _ => $"{epoch}   Earth satellites"
        };
    }

    private void LoadSatellites(Container controls, Container buttons)
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_SCIENCE_ROOT");
        string root = string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("res://.local-data/science")
            : configured;
        string catalog = Path.Combine(root, "catalog", "sciencesat.json");
        if (string.IsNullOrWhiteSpace(configured) && !File.Exists(catalog))
            return;

        try
        {
            foreach (ScienceSatellite track in ScienceSatelliteCatalog.Load(catalog, root))
            {
                ArrayMesh mesh = ScienceMeshLoader.Load(track.MeshPath);
                var node = new MeshInstance3D { Name = track.Name, Mesh = mesh, Visible = false };
                float diameter = mesh.GetAabb().Size.Length();
                if (!(diameter > 0 && float.IsFinite(diameter)))
                    throw new InvalidDataException("Satellite mesh has invalid bounds.");
                _satellites.Add(new(track, node, 18 / diameter));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                      or FormatException or OverflowException or System.Text.Json.JsonException)
        {
            foreach (SatelliteDisplay display in _satellites)
                display.Node.Free();
            _satellites.Clear();
            controls.AddChild(new Label { Text = $"Science catalogue unavailable. {error.Message}" });
        }

        foreach (SatelliteDisplay display in _satellites)
            AddChild(display.Node);
        if (_satellites.Count > 0)
            AddButton(buttons, "Earth satellites", SetSatelliteView);
    }

    private void DrawSatellites(JulianInstant epoch)
    {
        foreach (SatelliteDisplay display in _satellites)
        {
            if (_view != View.Satellites || !display.Track.TrySample(epoch, out var sample))
            {
                display.Node.Hide();
                continue;
            }

            Basis3d body = sample.BodyToTrajectory;
            Basis3d mesh = sample.MeshToBody;
            Vector3 Column(Vector3d value) => ToGodot(body.Transform(value), 1) * display.MarkerScale;
            var basis = new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z));
            display.Node.Transform = new Transform3D(basis, ToGodot(sample.EarthRelativeState.PositionKilometers,
                SatelliteKilometersPerUnit));
            display.Node.Show();
        }
    }

    private static Vector3 ToGodot(Vector3d value, double scale) =>
        new((float)(value.X / scale), (float)(value.Z / scale), (float)(-value.Y / scale));

    private void ToggleClock() => SetPlaying(!_playing);

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        _play.Text = playing ? "Pause" : "Play";
    }

    private Node3D CreateMarker(string name, Color color, float radius)
    {
        var marker = new Node3D { Name = name };
        marker.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = 2 * radius },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
        });
        marker.AddChild(new Label3D
        {
            Text = name,
            Position = new(0, 0, radius + 5),
            PixelSize = 0.27f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true
        });
        AddChild(marker);
        return marker;
    }

    private VBoxContainer CreateControls()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var controls = new VBoxContainer { Position = new(18, 14) };
        layer.AddChild(controls);
        return controls;
    }

    private static Button AddButton(Container parent, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += () => onPressed();
        parent.AddChild(button);
        return button;
    }
}
