// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene : Node3D
{
    private const double OverviewKilometersPerUnit = 1_000_000;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SimulatedSecondsPerSecond = JulianInstant.SecondsPerDay;
    private De440Ephemeris? _ephemeris;
    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!;
    private Label _status = null!;
    private Button _play = null!, _scale = null!;
    private bool _playing;
    private bool _lunarView;

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
        _scale = AddButton(buttons, "Earth–Moon view", () => SetLunarView(!_lunarView));
        Seek(new(JulianInstant.J2000Day, 0, TimeScale.Tdb));
    }

    public override void _Process(double delta)
    {
        if (_playing)
            Advance(delta * SimulatedSecondsPerSecond);
    }

    internal void Seek(JulianInstant epoch)
    {
        De440Ephemeris model = _ephemeris ?? throw new InvalidOperationException("Load DE440 before setting the clock.");
        CurrentSample = model.Sample(epoch);
        DrawSample();
    }

    internal void SetLunarView(bool enabled)
    {
        _lunarView = enabled;
        _scale.Text = enabled ? "Solar overview" : "Earth–Moon view";
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

        ObjectId origin = _lunarView ? SolarSystemSample.EarthId : SolarSystemSample.SunId;
        double scale = _lunarView ? LunarKilometersPerUnit : OverviewKilometersPerUnit;
        _sun.Position = ToGodot(sample.RelativeState(SolarSystemSample.SunId, origin).PositionKilometers, scale);
        _earth.Position = ToGodot(sample.RelativeState(SolarSystemSample.EarthId, origin).PositionKilometers, scale);
        _moon.Position = ToGodot(sample.RelativeState(SolarSystemSample.MoonId, origin).PositionKilometers, scale);
        _sun.Visible = !_lunarView;
        _moon.Visible = _lunarView;
        _camera.Size = _lunarView ? 250 : 365;
        string epoch = FormattableString.Invariant($"JD {sample.Epoch.ToJulianDate():F6} TDB");
        _status.Text = _lunarView ? $"{epoch}   Earth–Moon view" : $"{epoch}   Solar overview";
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
