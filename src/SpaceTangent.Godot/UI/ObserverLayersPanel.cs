// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.UI;

internal readonly record struct ObserverSkyVisibility(bool Stars, bool Labels, bool EquatorialGrid,
    bool Constellations, bool DeepSky, bool ZodiacalLight);
internal readonly record struct ObserverLayerCapabilities(bool Sky, bool Zodiacal, bool MinorBodies);
internal sealed record ObserverLayerPanelActions(Action<CameraMode> SetMode, Action<bool> SetOrbits,
    Action<MinorBodyDisplay> SetSmallBodies, Action<SkyMarkings> ToggleMark, Action<bool> SetStarlink,
    Action<ObserverSkyVisibility> SetVisibility);

internal sealed class ObserverLayersPanel
{
    private readonly ObserverLayerPanelActions _actions;
    private readonly ObserverLayerCapabilities _capabilities;
    private readonly CheckButton _showPlanetOrbits, _stars, _starLabels, _equatorialGrid, _constellations,
        _deepSky, _zodiacalLight, _starlink;
    private readonly OptionButton _smallBodyMode;
    private readonly PopupMenu _skyMarkingsMenu;
    private readonly Button _satelliteLayersMode;

    internal ObserverLayersPanel(SimulatorWorkspace workspace, ObserverLayerCapabilities capabilities,
        ObserverLayerPanelActions actions)
    {
        _actions = actions; _capabilities = capabilities;
        var _workspace = workspace;
        var layers = _workspace.AddPage("Layers", WorkspaceText.Choose("Layers and view", "Слои и вид"), "Search", WorkspaceText.Choose("Layers", "Слои"));
        layers.AddChild(SimulatorTheme.Section("Objects"));
        SimulatorButtons.Add(layers, "Inner planets", () => _actions.SetMode(CameraMode.Solar));
        SimulatorButtons.Add(layers, "Outer planets", () => _actions.SetMode(CameraMode.SolarOuter));
        _showPlanetOrbits = new CheckButton { Text = "Planet orbits", ButtonPressed = true };
        _showPlanetOrbits.Toggled += visible => _actions.SetOrbits(visible);
        layers.AddChild(_showPlanetOrbits);
        SimulatorButtons.Add(layers, "Earth–Moon view", () => _actions.SetMode(CameraMode.Lunar));
        layers.AddChild(SimulatorTheme.Section("Sky in this view"));
        _stars = SkyToggle(layers, "StarsLayer", "Bright stars");
        _starLabels = SkyToggle(layers, "StarLabelsLayer", "Star labels");
        _equatorialGrid = SkyToggle(layers, "EquatorialGridLayer", "Equatorial grid");
        _constellations = SkyToggle(layers, "ConstellationLayer", "Constellation figures");
        _deepSky = SkyToggle(layers, "DeepSkyLayer", "Deep-sky objects");
        _zodiacalLight = SkyToggle(layers, "ZodiacalLightLayer", "Zodiacal light");
        _zodiacalLight.Disabled = !capabilities.Zodiacal;
        _smallBodyMode = new OptionButton { Name = "SmallBodySwarmMode", Disabled = !capabilities.MinorBodies };
        _smallBodyMode.AddItem("Small-body swarm off");
        _smallBodyMode.AddItem("Small-body brightness");
        _smallBodyMode.AddItem("Small-body diagram");
        _smallBodyMode.ItemSelected += index => _actions.SetSmallBodies((MinorBodyDisplay)index);
        layers.AddChild(_smallBodyMode);
        var markings = new MenuButton { Name = "SkyMarkings", Text = "Sky markings" };
        _skyMarkingsMenu = markings.GetPopup();
        _skyMarkingsMenu.HideOnCheckableItemSelection = false;
        foreach (var (mark, title) in new (SkyMarkings Mark, string Title)[]
        {
            (SkyMarkings.EquatorOfDate, "Equator of date"), (SkyMarkings.GalacticEquator, "Galactic equator"),
            (SkyMarkings.Colures, "Colures"), (SkyMarkings.Poles, "Celestial, ecliptic and galactic poles"),
            (SkyMarkings.EquinoxesAndSolstices, "Equinoxes and solstices"), (SkyMarkings.ArchaeoLines, "Archaeoastronomy lines"),
            (SkyMarkings.NavigationalStars, "Navigational stars"), (SkyMarkings.Pulsars, "Pulsars"),
            (SkyMarkings.Quasars, "Quasars"), (SkyMarkings.SelectedBodyPath, "Selected body path"),
            (SkyMarkings.Meridian, "Local meridian"),
            (SkyMarkings.PrimeVertical, "Prime vertical"), (SkyMarkings.PrecessionCircles, "Precession circles"),
            (SkyMarkings.CircumpolarCircles, "Circumpolar circles"), (SkyMarkings.ZenithAndNadir, "Zenith and nadir")
        })
            _skyMarkingsMenu.AddCheckItem(title, (int)mark);
        _skyMarkingsMenu.IdPressed += id => _actions.ToggleMark((SkyMarkings)id);
        layers.AddChild(markings);
        layers.AddChild(SimulatorTheme.Section("Earth satellites"));
        _satelliteLayersMode = SimulatorButtons.Add(layers, "Earth satellites view", () => _actions.SetMode(CameraMode.Satellites));
        _starlink = new CheckButton { Name = "StarlinkLayer", Text = "Starlink" };
        _starlink.Toggled += enabled => _actions.SetStarlink(enabled);
        layers.AddChild(_starlink);
    }

    internal void SetSatelliteAvailability(bool available) => _satelliteLayersMode.Disabled = !available;

    internal void Update(SkyLayerSettings sky)
    {
        _showPlanetOrbits.SetPressedNoSignal(!sky.HideOrbits);
        _stars.SetPressedNoSignal(sky.Stars);
        _starLabels.SetPressedNoSignal(sky.Labels);
        _equatorialGrid.SetPressedNoSignal(sky.EquatorialGrid);
        _constellations.SetPressedNoSignal(sky.Constellations);
        _deepSky.SetPressedNoSignal(sky.DeepSky);
        _zodiacalLight.SetPressedNoSignal(sky.ZodiacalLight);
        _starlink.SetPressedNoSignal(sky.Starlink ?? !sky.HideSatellites);
        _smallBodyMode.Select((int)(sky.SmallBodies ?? MinorBodyDisplay.Photometric));
        for (int index = 0; index < _skyMarkingsMenu.ItemCount; index++)
            _skyMarkingsMenu.SetItemChecked(index, sky.Markings.HasFlag((SkyMarkings)_skyMarkingsMenu.GetItemId(index)));
    }

    private CheckButton SkyToggle(Container panel, string name, string title)
    {
        var button = new CheckButton { Name = name, Text = title, Disabled = !_capabilities.Sky };
        button.Toggled += _ => _actions.SetVisibility(new(_stars.ButtonPressed, _starLabels.ButtonPressed,
            _equatorialGrid.ButtonPressed, _constellations.ButtonPressed, _deepSky.ButtonPressed, _zodiacalLight.ButtonPressed));
        panel.AddChild(button);
        return button;
    }
}
