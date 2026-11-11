// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.UI;

internal sealed class EarthObserverPanel
{
    private readonly Control _ephemerides;
    private readonly OptionButton _sites;
    private readonly LineEdit _latitude, _longitude, _height, _azimuth, _elevation;
    private readonly CheckButton _horizon, _cardinals;
    private readonly Label _coordinates, _twilight;
    private readonly Action<EarthObserverCamera> _apply;
    private readonly Action<string> _setStatus;
    private EarthObserverCamera _camera;
    private int _view = -1;
    private (DateOnly Date, EarthSite Site, DeltaTModel Model)? _twilightKey;

    internal EarthObserverPanel(VBoxContainer location, VBoxContainer ephemerides, bool surfaceAvailable,
        Action<EarthObserverCamera> apply, Action fullMoon, Action<string> setStatus)
    {
        _apply = apply;
        _setStatus = setStatus;
        _ephemerides = ephemerides;
        location.AddChild(SimulatorTheme.Section("Observing site"));
        _sites = new OptionButton { Name = "ObserverSite", Disabled = !surfaceAvailable };
        _sites.AddItem("Custom");
        _sites.AddItem("Moscow");
        _sites.AddItem("Tromsø");
        _sites.SetItemDisabled(0, true);
        _sites.ItemSelected += index => _apply(_camera with
        {
            Site = index == 1 ? EarthObserverCamera.Moscow.Site : EarthObserverCamera.Tromso.Site
        });
        location.AddChild(_sites);
        _latitude = Field(location, "ObserverLatitude", "Latitude (°)", surfaceAvailable);
        _longitude = Field(location, "ObserverLongitude", "Longitude east (°)", surfaceAvailable);
        _height = Field(location, "ObserverHeight", "Height (km)", surfaceAvailable);

        location.AddChild(SimulatorTheme.Section("Camera direction"));
        _azimuth = Field(location, "ObserverAzimuth", "Azimuth (°)", surfaceAvailable);
        _elevation = Field(location, "ObserverElevation", "Elevation (°)", surfaceAvailable);
        var submit = new Button { Name = "ApplyObserver", Text = "Apply to this view", Disabled = !surfaceAvailable };
        submit.Pressed += ApplyFields;
        location.AddChild(submit);
        var moon = new Button { Name = "ShowFullMoon", Text = "Full Moon · Moscow", Disabled = !surfaceAvailable };
        moon.Pressed += fullMoon;
        location.AddChild(moon);

        location.AddChild(SimulatorTheme.Section("Horizon"));
        _horizon = new CheckButton { Name = "HorizonGrid", Text = "Horizon grid", Disabled = !surfaceAvailable };
        _horizon.Toggled += shown => _apply(_camera with { HorizonGrid = shown });
        location.AddChild(_horizon);
        _cardinals = new CheckButton { Name = "CardinalPoints", Text = "Cardinal points", Disabled = !surfaceAvailable };
        _cardinals.Toggled += shown => _apply(_camera with { CardinalPoints = shown });
        location.AddChild(_cardinals);

        ephemerides.AddChild(SimulatorTheme.Section("Selected object"));
        _coordinates = SimulatorTheme.Detail("");
        _coordinates.Name = "HorizontalCoordinates";
        ephemerides.AddChild(_coordinates);
        ephemerides.AddChild(SimulatorTheme.Section("Civil twilight"));
        _twilight = SimulatorTheme.Detail("");
        _twilight.Name = "CivilTwilight";
        ephemerides.AddChild(_twilight);
    }

    internal void Update(int view, ObserverView observer, WorldSnapshot? snapshot, De440Ephemeris ephemeris,
        DeltaTModel model, BrightStarCatalogue brightStars)
    {
        EarthObserverCamera camera = observer.EarthObserver ?? EarthObserverCamera.Moscow;
        if (_view != view || camera.Site != _camera.Site || camera.AzimuthDegrees != _camera.AzimuthDegrees ||
            camera.ElevationDegrees != _camera.ElevationDegrees)
        {
            _view = view;
            _sites.Select(camera.Site == EarthObserverCamera.Moscow.Site ? 1 :
                camera.Site == EarthObserverCamera.Tromso.Site ? 2 : 0);
            _latitude.Text = Format(camera.Site.LatitudeDegrees);
            _longitude.Text = Format(camera.Site.LongitudeDegrees);
            _height.Text = Format(camera.Site.HeightKilometers);
            _azimuth.Text = Format(camera.AzimuthDegrees);
            _elevation.Text = Format(camera.ElevationDegrees);
        }
        _camera = camera;
        _horizon.SetPressedNoSignal(camera.HorizonGrid);
        _cardinals.SetPressedNoSignal(camera.CardinalPoints);
        if (!_ephemerides.IsVisibleInTree())
            return;

        _coordinates.Text = "Select an object to see its geometric altitude and azimuth.";
        if (observer.Selection is { } target)
        {
            _coordinates.Text = "The selected object has no horizontal coordinates at this epoch.";
            if (snapshot?.SolarSystem is { } solar)
            {
                try
                {
                    SurfaceLighting light = EarthSurfaceLighting.Sample(solar, camera.Site, model);
                    var coordinates = brightStars.Find(target) is { } star
                        ? EarthObserving.HorizontalDirection(light, star.DirectionAt(observer.Epoch))
                        : snapshot.TryState(target, out CartesianState state)
                            ? EarthObserving.Horizontal(light, (state - solar.Earth).PositionKilometers) : null;
                    if (coordinates is { } horizontal)
                    {
                        string azimuth = horizontal.AzimuthDegrees is { } az
                            ? FormattableString.Invariant($"{az:F2}°") : "undefined";
                        _coordinates.Text = FormattableString.Invariant(
                            $"Altitude {horizontal.AltitudeDegrees:F2}°\nAzimuth {azimuth}");
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    // The surface model can cover fewer epochs than the loaded ephemeris.
                }
            }
        }
        try
        {
            DateOnly date = EarthObserving.LocalMeanDate(observer.Epoch, camera.Site);
            var key = (date, camera.Site, model);
            if (key == _twilightKey)
                return;
            _twilightKey = key;
            CivilTwilight twilight = EarthObserving.Twilight(ephemeris, date, camera.Site, model);
            _twilight.Text = FormattableString.Invariant($"{date:yyyy-MM-dd} · local mean solar date\n") +
                (twilight.Horizon.AlwaysAbove ? "Sun above the horizon all day." :
                 twilight.CivilLimit.AlwaysBelow ? "Sun below the civil limit all day." :
                 twilight.Horizon.AlwaysBelow
                     ? Interval("Sun below the horizon all day.\nCivil light", twilight.CivilLimit.Rising, twilight.CivilLimit.Setting)
                     : Interval("Morning", twilight.CivilLimit.Rising, twilight.Horizon.Rising) + "\n" +
                       Interval("Evening", twilight.Horizon.Setting, twilight.CivilLimit.Setting));
        }
        catch (ArgumentOutOfRangeException)
        {
            _twilight.Text = "Twilight is unavailable for this date.";
        }
    }

    private void ApplyFields()
    {
        if (Read(_latitude, out double latitude) && Read(_longitude, out double longitude) &&
            Read(_height, out double height) && Read(_azimuth, out double azimuth) && Read(_elevation, out double elevation))
        {
            var camera = _camera with { Site = new(latitude, longitude, height), AzimuthDegrees = azimuth, ElevationDegrees = elevation };
            if (camera.IsValid())
            {
                _apply(camera);
                return;
            }
        }
        _setStatus("Use latitude −90–90°, longitude −180–180°, height 0–100 km, azimuth 0–<360° and elevation −90–90°.");
    }

    private static LineEdit Field(Container parent, string name, string title, bool enabled)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        var label = SimulatorTheme.Detail(title);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(label);
        var field = new LineEdit { Name = name, Editable = enabled, CustomMinimumSize = new(100, 0), MaxLength = 24 };
        row.AddChild(field);
        return field;
    }

    private static string Interval(string label, JulianInstant? start, JulianInstant? end) =>
        start is not null || end is not null
            ? $"{label} · {Time(start)} – {Time(end)}"
            : $"{label} · no crossings";

    private static string Time(JulianInstant? instant)
    {
        if (instant is not { } time)
            return "—";
        UtcDateTime utc = TimeConversion.ToUtc(time);
        return FormattableString.Invariant($"{utc.Date:MM-dd} {utc.Hour:00}:{utc.Minute:00} UTC");
    }

    private static bool Read(LineEdit field, out double value) =>
        double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}
