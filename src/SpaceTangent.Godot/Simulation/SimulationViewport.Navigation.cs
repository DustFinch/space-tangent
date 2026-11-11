// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const float NavigationCameraUnits = 48;
    private const float NavigationFar = 12000;
    private Camera3D _navigationCamera = null!;
    private DirectionalLight3D _navigationLight = null!;
    private MeshInstance3D _navigationSun = null!, _navigationEarth = null!, _navigationMoon = null!;

    private EarthOrbitRenderer? _earthOrbit;

    internal void SetEarthOrbitTextures(EarthOrbitTextures textures) => _earthOrbit = new(_navigationEarth, textures);

    internal bool EarthOrbitAtmosphereActive => _earthOrbit?.Active == true;
    internal bool NavigationCameraCurrent => _navigationCamera.Current;

    private void InitializeNavigation()
    {
        _navigationCamera = new Camera3D { Fov = 49, Near = .02f, Far = NavigationFar, KeepAspect = Camera3D.KeepAspectEnum.Height };
        AddChild(_navigationCamera);
        _navigationLight = new DirectionalLight3D { LightEnergy = 1.05f, Visible = false };
        AddChild(_navigationLight);
        _navigationSun = CreateNavigationSphere(new Color("ffd18d"), emissive: true);
        _navigationEarth = CreateNavigationSphere(new Color("4a76ad"));
        _navigationMoon = CreateNavigationSphere(new Color("c9c4b6"));
        InitializeStellarRendering();
    }

    private MeshInstance3D CreateNavigationSphere(Color color, bool emissive = false)
    {
        var mesh = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 64, Rings = 32 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                ShadingMode = emissive ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                EmissionEnabled = !emissive, Emission = color, EmissionEnergyMultiplier = .14f
            },
            Visible = false
        };
        AddChild(mesh);
        return mesh;
    }

    private void DrawNavigation(NavigationCamera pose, WorldSnapshot snapshot, SolarSystemSample solar, DeltaTModel deltaT,
        SkyLayerSettings sky, double fieldOfView)
    {
        double units = pose.RangeKilometers / NavigationCameraUnits;
        double azimuth = pose.AzimuthRadians, elevation = pose.ElevationRadians;
        var offset = new Vector3(
            (float)(Math.Cos(elevation) * Math.Sin(azimuth)),
            (float)Math.Sin(elevation),
            (float)(Math.Cos(elevation) * Math.Cos(azimuth)));
        _navigationCamera.Fov = (float)fieldOfView;
        if (pose.Placement is not null)
        {
            NavigationPlacement placement = pose.ResolvePlacement(snapshot);
            _navigationCamera.Position = ToGodot(placement.OffsetKilometers, units);
            _navigationCamera.LookAt(_navigationCamera.Position + ToGodot(placement.Forward, 1), ToGodot(placement.Up, 1));
        }
        else
        {
            _navigationCamera.Position = offset * NavigationCameraUnits;
            _navigationCamera.LookAt(Vector3.Zero);
        }
        if (!_navigationCamera.Current)
            _navigationCamera.MakeCurrent();

        Vector3 light = ToGodot(solar.Sun.PositionKilometers - pose.AnchorKilometers, units);
        if (light.LengthSquared() > 1e-6f)
        {
            light = light.Normalized();
            _navigationLight.LookAt(-light, Math.Abs(light.Y) > .98f ? Vector3.Forward : Vector3.Up);
        }
        _navigationLight.Show();
        PlaceBody(_navigationSun, solar.Sun.PositionKilometers, SolarSystemSample.SunId);
        PlaceBody(_navigationEarth, solar.Earth.PositionKilometers, SolarSystemSample.EarthId);
        if (_earthOrbit is not null)
        {
            float projectedDistance = _navigationEarth.Position.DistanceTo(_navigationCamera.Position);
            _earthOrbit.Update(solar, deltaT, _navigationEarth.Scale.X > projectedDistance * .004f);
        }
        PlaceBody(_navigationMoon, solar.Moon.PositionKilometers, SolarSystemSample.MoonId);
        HidePlanetGlobes();
        DrawPlanetGlobes(snapshot, pose, units, ProjectWithScale);
        DrawStellarObjects(pose, snapshot, sky, units, ProjectWithScale);
        DrawSscBodies(snapshot, pose, units, ProjectWithScale);

        (Vector3 Position, double Scale) ProjectWithScale(Vector3d position)
        {
            Vector3 projected = Project(position, out double scale);
            return (projected, scale);
        }

        foreach (var (asset, node) in _satellites.Values)
        {
            if (!snapshot.TryState(asset.Track.Id, out var state) || !snapshot.TryOrientation(asset.Track.Id, out var orientation))
                continue;
            double radius = NavigationCamera.BodyRadiusKilometers(asset.Track.Id);
            if (radius / pose.RangeKilometers < .0002 && pose.Target != asset.Track.Id)
                continue;
            Vector3 position = Project(state.PositionKilometers, out double scale);
            float modelScale = (float)(radius / units * scale) * asset.MarkerScale / 9;
            if (modelScale < 1e-7f)
                continue;
            Basis3d body = orientation.ToWorld, mesh = asset.Track.MeshToBody;
            Vector3 Column(Vector3d axis) => ToGodot(body.Transform(axis), 1) * modelScale;
            node.Transform = new(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)), position);
            node.Show();
        }

        void PlaceBody(MeshInstance3D node, Vector3d position, ObjectId id)
        {
            node.Position = Project(position, out double scale);
            float radius = (float)(NavigationCamera.BodyRadiusKilometers(id) * scale / units);
            node.Scale = Vector3.One * Math.Max(.035f, radius);
            node.Show();
        }

        Vector3 Project(Vector3d position, out double scale)
        {
            Vector3d relative = position - pose.AnchorKilometers;
            double distance = relative.Length / units;
            // Fit distant bodies in the camera range while retaining their angular size.
            scale = distance > NavigationFar * .8 ? NavigationFar * .8 / distance : 1;
            return ToGodot(relative * scale, units);
        }
    }

    private void HideNavigation()
    {
        _navigationLight.Hide();
        _navigationSun.Hide();
        _navigationEarth.Hide();
        _navigationMoon.Hide();
        HidePlanetGlobes();
        HideStellarObjects();
        HideSscBodies();
    }
}
