// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedSatellite(ScienceSatellite Track, ArrayMesh Mesh, float MarkerScale);
internal sealed record PreparedMission(ObjectId Id, ArrayMesh Mesh, float MarkerScale, ArrayMesh? Frusta,
    SaturnSurface? Surface, IReadOnlyList<SpiceSensor> Sensors);
internal sealed record PreparedProbe(SscHuygensTrack Track, ArrayMesh Mesh, float MarkerScale);

internal partial class SimulationViewport : Node3D
{
    private const double OverviewKilometersPerUnit = PlanetOverviewRendering.InnerScale;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SatelliteKilometersPerUnit = 75;
    private const double MissionKilometersPerUnit = MissionCameraRendering.KilometersPerUnit;

    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!, _saturn = null!;
    private MissionCameraRendering _missionRendering = null!;
    private SurfaceProjection? _surface;
    private BodyTerrainProjection? _terrain;
    private BodyTerrainAsset? _terrainAsset;
    private BodyTerrainProjection? _marsTerrain;
    private BodyTerrainAsset? _marsTerrainAsset;
    private Texture2D? _marsSurface;
    private IReadOnlyList<PreparedSatellite>? _satelliteAssets;
    private readonly Dictionary<ObjectId, (PreparedSatellite Asset, MeshInstance3D Node)> _satellites = new();
    internal MeshInstance3D? ScienceBodyNode(ObjectId id) => _satellites.TryGetValue(id, out var entry) ? entry.Node : null;

    internal ISolarSystemEphemeris? PlanetEphemeris { get; set; }
    internal Vector3 EarthPosition => _earth.Position;
    internal Vector3 MoonPosition => _moon.Position;
    internal bool MoonVisible => _moon.Visible;
    internal string? ProbeStatus => _missionRendering?.ProbeStatus;
    internal BodyTerrainProjection? Terrain => _terrain;
    internal BodyTerrainProjection? MarsTerrain => _marsTerrain;

    public override void _Ready()
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Position = new(0, 400, 0),
            KeepAspect = Camera3D.KeepAspectEnum.Width
        };
        AddChild(_camera);
        _camera.LookAt(Vector3.Zero, Vector3.Forward);
        _camera.MakeCurrent();
        WatchViewportSize();
        FitCamera();
        _sun = CreateMarker("Sun", new Color("#ffce69"), 4);
        _earth = CreateMarker("Earth", new Color("#6fa8ff"), 3);
        _moon = CreateMarker("Moon", new Color("#cbd3e4"), 2.4f);
        InitializeNavigation();
        InitializePlanets();
        _saturn = CreateMarker("Saturn barycenter", new Color("#d9bb7f"), 9);
        _missionRendering = new(this, _camera, _sun, _saturn, HideOrbitalMarkers, CreateLabel, LineMaterial);
    }

    private Viewport? _sizeEvents;
    private bool _renderResourcesClosed;

    public override void _EnterTree()
    {
        // Layout rebuilds detach and reattach the same view. Native resources
        // survive that transition; only the viewport subscription is rebound.
        if (_camera is not null && !_renderResourcesClosed) WatchViewportSize();
    }

    public override void _ExitTree() => DisconnectViewportSize();

    private void WatchViewportSize()
    {
        var viewport = GetViewport();
        if (ReferenceEquals(viewport, _sizeEvents)) return;
        DisconnectViewportSize();
        _sizeEvents = viewport;
        viewport.SizeChanged += FitCamera;
    }

    private void DisconnectViewportSize()
    {
        if (_sizeEvents is { } viewport && GodotObject.IsInstanceValid(viewport))
            viewport.SizeChanged -= FitCamera;
        _sizeEvents = null;
    }

    // Called by the view owner only for final removal/shutdown, not reparenting.
    internal void CloseOwnedResources()
    {
        if (_renderResourcesClosed) return;
        _renderResourcesClosed = true;
        DisconnectViewportSize();
        _missionRendering?.Close();
        _missionTrailRendering?.Close();
        _opticalRendering?.Close();
        _referenceMarkRendering?.Close();
        _stars?.Close();
        _recordedStars?.Close();
        _planetOverview?.Close();
        _skyPresentation?.Close();
        ReleaseImportedDrawingResources();
    }

    private void FitCamera()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        _camera.KeepAspect = size.X >= size.Y ? Camera3D.KeepAspectEnum.Height : Camera3D.KeepAspectEnum.Width;
        _terrain?.FitCamera(size);
        _marsTerrain?.FitCamera(size);
    }

    internal void ReplaceSatellites(IReadOnlyList<PreparedSatellite> assets)
    {
        if (ReferenceEquals(_satelliteAssets, assets))
            return;
        _satelliteAssets = assets;
        foreach (var (_, node) in _satellites.Values)
        {
            RemoveChild(node);
            node.QueueFree();
        }
        _satellites.Clear();

        foreach (PreparedSatellite asset in assets)
        {
            var node = new MeshInstance3D { Mesh = asset.Mesh, Visible = false };
            _satellites.Add(asset.Track.Id, (asset, node));
            AddChild(node);
        }
    }

    internal MeshInstance3D SatelliteMesh(ObjectId id) => _satellites[id].Node;

    internal MeshInstance3D MissionMesh => _missionRendering.MissionMesh;
    internal bool MissionUsesSun => _sun.Visible;
    internal bool MissionUsesSaturn => _missionRendering.MissionUsesSaturn;
    internal MeshInstance3D MissionFrusta => _missionRendering.MissionFrusta;
    internal MeshInstance3D MissionProjectedFrusta => _missionRendering.MissionProjectedFrusta;
    internal bool MissionFootprintVisible => _missionRendering.MissionFootprintVisible;
    internal bool MissionProjectionVisible => _missionRendering.MissionProjectionVisible;
    internal int MissionFootprintCount => _missionRendering.MissionFootprintCount;

    internal void ReplaceMission(PreparedMission? asset, PreparedMission? timeline, PreparedProbe? probe) =>
        _missionRendering.ReplaceMission(asset, timeline, probe);

    internal static ArrayMesh? CreateFrusta(IReadOnlyList<SpiceSensor> sensors, GpuAssetPublication? publication = null)
    {
        if (sensors.Count == 0)
            return null;
        using var asset = new GpuAssetPublication.AssetScope(publication);
        var mesh = new ArrayMesh();
        asset.Own(mesh);
        foreach (SpiceSensor sensor in sensors)
        {
            // Body coordinates in the same kilometers-per-unit scale as the mission view.
            float range = (float)(sensor.RangeKilometers / MissionKilometersPerUnit);
            if (!float.IsFinite(range) || range <= 0)
                throw new InvalidDataException("SPICE sensor range cannot be rendered at the mission scale.");
            Vector3[] rim = sensor.CornersInBody.Select(ray => new Vector3((float)ray.X, (float)ray.Y, (float)ray.Z) * range).ToArray();
            var lines = new Vector3[rim.Length * 4];
            for (int corner = 0; corner < rim.Length; corner++)
            {
                lines[4 * corner] = Vector3.Zero;
                lines[4 * corner + 1] = rim[corner];
                lines[4 * corner + 2] = rim[corner];
                lines[4 * corner + 3] = rim[(corner + 1) % rim.Length];
            }
            var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = lines;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
            var material = LineMaterial(sensor.Color, sensor.FrustumOpacity);
            asset.Own(material);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }
        return asset.Complete(mesh);
    }

    private static StandardMaterial3D LineMaterial(Vector3d color, double opacity) => new()
    {
        AlbedoColor = new((float)color.X, (float)color.Y, (float)color.Z, (float)opacity),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha
    };

    internal void SetSurfaceTextures(SurfaceTextures textures, Texture2D? groundRingAtlas = null)
    {
        _surface = new SurfaceProjection(this, textures, groundRingAtlas);
        _archivedMoonMap = textures.Moon;
        _navigationMoonMaterial.SetShaderParameter("surface_map", textures.Moon);
        _navigationMoonMaterial.SetShaderParameter("has_surface_map", true);
    }

    internal void ReplaceLunarTerrain(BodyTerrainAsset? asset)
    {
        if (ReferenceEquals(_terrainAsset, asset))
            return;
        _terrainAsset = asset;
        if (asset is not null)
        {
            _terrain ??= new(this);
            _terrain.FitCamera(GetViewport().GetVisibleRect().Size);
        }
        _terrain?.Replace(asset);
    }

    internal void ReplaceMarsTerrain(BodyTerrainAsset? asset)
    {
        if (ReferenceEquals(_marsTerrainAsset, asset))
            return;
        _marsTerrainAsset = asset;
        if (asset is not null)
        {
            _marsTerrain ??= new(this, _marsSurface, _marsTiles);
            _marsTerrain.FitCamera(GetViewport().GetVisibleRect().Size);
        }
        _marsTerrain?.Replace(asset);
    }

    internal (Vector3d Ray, HorizontalCoordinates? Horizon)? PointerDirection(Vector2 position)
    {
        if (_surface?.PointerDirection(position) is { } ground)
            return ground;
        if (!_navigationCamera.Current)
            return null;
        Vector3 direction = _navigationCamera.ProjectRayNormal(position);
        Vector3d ray = new(direction.X, -direction.Z, direction.Y);
        return (ray, _bodyObserver?.Frame.Direction(ray));
    }

    internal void SetSurfaceControls(bool clouds, bool lunarLight) => _surface!.SetControls(clouds, lunarLight);

    internal void SetLandscape(LandscapeTextures? textures, bool showFog) => _surface?.SetLandscape(textures, showFog);

    internal bool LandscapeFogVisible => _surface?.LandscapeFogVisible == true;

    internal string? VisibleLandscape => _surface?.VisibleLandscape;

    internal int ActiveMeteorTrailCount => _surface?.ActiveMeteorTrailCount ?? 0;
    internal bool ShowsMeteorRadiant(string code) => _surface?.ShowsMeteorRadiant(code) == true;

    internal void SetSurfacePitch(double degrees) => _surface!.PitchDegrees = degrees;

    private bool DrawScene(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        if (observer.Navigation is null) CancelMissionTrail();
        if (_surface is not null) _surface.IncrementalPaths = IncrementalPaths && ArchivedGeometry is null;
        SolarSystemSample sample = snapshot.SolarSystem ?? throw new InvalidOperationException("The view needs a solar-system snapshot.");
        _saturn.Hide();
        _missionRendering.Hide();
        _terrain?.Hide();
        _marsTerrain?.Hide();
        HidePlanetOverview();
        HideSurfaceSites();
        if (observer.Navigation is { } navigation)
        {
            _surface?.Draw(false, sample, deltaT);
            HideOrbitalMarkers();
            DrawNavigation(navigation, snapshot, sample, deltaT, observer.Sky, observer.Selection, OpticalField(observer),
                observer.ActiveOptics?.Inverted == true);
            DrawSurfaceSites(navigation, observer.Selection, snapshot, deltaT);
            return true;
        }
        HideNavigation();
        if (observer.Mode is not (CameraMode.LunarTerrain or CameraMode.MarsTerrain) && !_camera.Current)
            _camera.MakeCurrent();
        if (_surface is { } surface)
        {
            bool isSurface = observer.Mode == CameraMode.Surface;
            bool available = surface.Draw(isSurface, sample, deltaT, observer.ObserverCamera, observer.Sky, observer.Selection,
                OpticalField(observer), observer.ActiveOptics, CatalogueWorld, snapshot,
                trueAngularScale: observer.Optics?.Finders is { Visible: true });
            if (isSurface && available)
                return true;
        }

        if (observer.Mode is CameraMode.Mission or CameraMode.Timeline)
            return _missionRendering.DrawMission(snapshot, observer.Mode == CameraMode.Timeline);
        if (observer.Mode == CameraMode.Probe)
            return _missionRendering.DrawProbe(snapshot);
        if (observer.Mode == CameraMode.LunarTerrain)
        {
            HideOrbitalMarkers();
            if (_terrain is null || !snapshot.TryOrientation(SolarSystemSample.MoonId, out var moonFrame))
                return false;
            Vector3d sun = (sample.Sun - sample.Moon).PositionKilometers;
            return _terrain.Draw(observer.Terrain ?? LunarTerrainCamera.Globe, moonFrame.ToWorld.Transpose().Transform(sun),
                (float)EclipseRendering.MoonSunFraction(sample), showRelief: !observer.Sky.HideTerrain);
        }

        if (observer.Mode == CameraMode.MarsTerrain)
        {
            HideOrbitalMarkers();
            if (_marsTerrain is null || !snapshot.TryOrientation(PlanetCatalogue.MarsId, out var frame) ||
                !snapshot.TryState(PlanetCatalogue.MarsId, out CartesianState mars))
                return false;
            Vector3d sun = (sample.Sun - mars).PositionKilometers;
            return _marsTerrain.Draw(new(-133.8, 18.65, 950), frame.ToWorld.Transpose().Transform(sun),
                showRelief: !observer.Sky.HideTerrain);
        }

        bool solar = observer.Mode is CameraMode.Solar or CameraMode.SolarOuter;
        bool outer = observer.Mode == CameraMode.SolarOuter;
        ObjectId origin = solar ? SolarSystemSample.SunId : SolarSystemSample.EarthId;
        double scale = solar
            ? (outer ? OuterOverviewKilometersPerUnit : OverviewKilometersPerUnit)
            : LunarKilometersPerUnit;
        if (solar && PlanetEphemeris is { } ephemeris)
            DrawPlanetOverview(snapshot, ephemeris, sample.Sun.PositionKilometers, outer);
        _sun.Position = ToGodot(sample.RelativeState(SolarSystemSample.SunId, origin).PositionKilometers, scale);
        _earth.Position = ToGodot(sample.RelativeState(SolarSystemSample.EarthId, origin).PositionKilometers, scale);
        _moon.Position = ToGodot(sample.RelativeState(SolarSystemSample.MoonId, origin).PositionKilometers, scale);
        _earth.Visible = !outer;
        _sun.Visible = solar;
        _moon.Visible = observer.Mode == CameraMode.Lunar;
        _camera.Size = solar ? (outer ? 410 : 520) : 250;

        foreach (var (asset, node) in _satellites.Values)
        {
            if (observer.Mode != CameraMode.Satellites ||
                !snapshot.TryState(asset.Track.Id, out var vehicle) ||
                !snapshot.TryOrientation(asset.Track.Id, out var rotation))
            {
                node.Hide();
                continue;
            }

            CartesianState earthRelative = vehicle - sample.Earth;
            Basis3d body = rotation.ToWorld;
            Basis3d mesh = asset.Track.MeshToOrientedBody;
            float marker = asset.MarkerScale * (observer.Selection == asset.Track.Id ? 1f : 0.75f);
            Vector3 Column(Vector3d value) => ToGodot(body.Transform(value), 1) * marker;
            node.Transform = new Transform3D(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)),
                ToGodot(earthRelative.PositionKilometers, SatelliteKilometersPerUnit));
            node.Show();
        }
        return observer.Mode != CameraMode.Surface;
    }

    private void HideOrbitalMarkers()
    {
        _sun.Hide();
        _earth.Hide();
        _moon.Hide();
        foreach (var (_, node) in _satellites.Values)
            node.Hide();
    }

    private static Vector3 ToGodot(Vector3d value, double scale) =>
        new((float)(value.X / scale), (float)(value.Z / scale), (float)(-value.Y / scale));

    private Node3D CreateMarker(string name, Color color, float radius)
    {
        var marker = new Node3D { Name = name };
        marker.AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius, Height = 2 * radius },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded }
        });
        marker.AddChild(CreateLabel(name, new(0, 0, radius + 5)));
        AddChild(marker);
        return marker;
    }

    private static Label3D CreateLabel(string name, Vector3 position) => new()
    {
        Text = name,
        Position = position,
        PixelSize = 0.27f,
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = true
    };
}
