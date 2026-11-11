// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
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
    private const double OverviewKilometersPerUnit = 1_000_000;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SatelliteKilometersPerUnit = 75;
    private const double MissionKilometersPerUnit = 2500;

    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!, _saturn = null!;
    private PreparedMission? _mission;
    private PreparedMission? _timeline;
    private PreparedProbe? _probe;
    private MeshInstance3D _missionNode = null!, _missionFrusta = null!;
    private Label3D _missionLabel = null!;
    private MeshInstance3D _saturnSurface = null!, _projectedFrusta = null!, _footprints = null!;
    private readonly ImmediateMesh _projectionMesh = new(), _footprintMesh = new();
    private StandardMaterial3D[] _footprintMaterials = [];
    private SurfaceProjection? _surface;
    private BodyTerrainProjection? _terrain;
    private BodyTerrainAsset? _terrainAsset;
    private BodyTerrainProjection? _marsTerrain;
    private BodyTerrainAsset? _marsTerrainAsset;
    private Texture2D? _marsSurface;
    private IReadOnlyList<PreparedSatellite>? _satelliteAssets;
    private readonly Dictionary<ObjectId, (PreparedSatellite Asset, MeshInstance3D Node)> _satellites = new();

    internal De440Ephemeris? PlanetEphemeris { get; set; }
    internal Vector3 EarthPosition => _earth.Position;
    internal Vector3 MoonPosition => _moon.Position;
    internal bool MoonVisible => _moon.Visible;
    internal string? ProbeStatus { get; private set; }
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
        GetViewport().SizeChanged += FitCamera;
        FitCamera();
        _sun = CreateMarker("Sun", new Color("#ffce69"), 4);
        _earth = CreateMarker("Earth", new Color("#6fa8ff"), 3);
        _moon = CreateMarker("Moon", new Color("#cbd3e4"), 2.4f);
        InitializeNavigation();
        InitializePlanets();
        _saturn = CreateMarker("Saturn barycenter", new Color("#d9bb7f"), 9);
        _missionNode = new MeshInstance3D { Visible = false };
        AddChild(_missionNode);
        _missionLabel = CreateLabel("Cassini", Vector3.Zero);
        _missionLabel.Hide();
        AddChild(_missionLabel);
        _missionFrusta = new MeshInstance3D { Visible = false };
        AddChild(_missionFrusta);
        _saturnSurface = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1, Height = 2, RadialSegments = 128, Rings = 64 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("#806e52"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            Visible = false
        };
        _projectedFrusta = new MeshInstance3D { Mesh = _projectionMesh, Visible = false };
        _footprints = new MeshInstance3D { Mesh = _footprintMesh, Visible = false };
        AddChild(_saturnSurface);
        AddChild(_projectedFrusta);
        AddChild(_footprints);
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

    internal MeshInstance3D MissionMesh => _missionNode;
    internal bool MissionUsesSun => _sun.Visible;
    internal bool MissionUsesSaturn => _saturn.Visible || _saturnSurface.Visible;
    internal MeshInstance3D MissionFrusta => _missionFrusta;
    internal MeshInstance3D MissionProjectedFrusta => _projectedFrusta;
    internal bool MissionFootprintVisible => _footprints.Visible;
    internal bool MissionProjectionVisible => _projectedFrusta.Visible;
    internal int MissionFootprintCount => _footprintMesh.GetSurfaceCount();

    internal void ReplaceMission(PreparedMission? asset, PreparedMission? timeline, PreparedProbe? probe)
    {
        if (ReferenceEquals(_mission, asset) && ReferenceEquals(_timeline, timeline) && ReferenceEquals(_probe, probe))
            return;
        _mission = asset;
        _timeline = timeline;
        _probe = probe;
        _missionNode.Hide();
        _missionLabel.Hide();
        _missionFrusta.Mesh = asset?.Frusta;
        _missionFrusta.Hide();
        _footprintMaterials = asset?.Sensors.Select(sensor => LineMaterial(sensor.Color, sensor.FootprintOpacity)).ToArray() ?? [];
    }

    internal static ArrayMesh? CreateFrusta(IReadOnlyList<SpiceSensor> sensors)
    {
        if (sensors.Count == 0)
            return null;
        var mesh = new ArrayMesh();
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
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, LineMaterial(sensor.Color, sensor.FrustumOpacity));
        }
        return mesh;
    }

    private static StandardMaterial3D LineMaterial(Vector3d color, double opacity) => new()
    {
        AlbedoColor = new((float)color.X, (float)color.Y, (float)color.Z, (float)opacity),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha
    };

    internal void SetSurfaceTextures(SurfaceTextures textures)
    {
        _surface = new SurfaceProjection(this, textures);
        if (_navigationMoon.MaterialOverride is StandardMaterial3D moon)
            moon.AlbedoTexture = textures.Moon;
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
            _marsTerrain ??= new(this, _marsSurface);
            _marsTerrain.FitCamera(GetViewport().GetVisibleRect().Size);
        }
        _marsTerrain?.Replace(asset);
    }

    internal void SetSurfaceControls(bool clouds, bool lunarLight) => _surface!.SetControls(clouds, lunarLight);

    internal void SetSurfacePitch(double degrees) => _surface!.PitchDegrees = degrees;

    private bool DrawScene(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        SolarSystemSample sample = snapshot.SolarSystem ?? throw new InvalidOperationException("The view needs a solar-system snapshot.");
        _saturn.Hide();
        _missionNode.Hide();
        _missionLabel.Hide();
        _missionFrusta.Hide();
        _saturnSurface.Hide();
        _projectedFrusta.Hide();
        _footprints.Hide();
        ProbeStatus = null;
        _terrain?.Hide();
        _marsTerrain?.Hide();
        HidePlanetOverview();
        if (observer.Navigation is { } navigation)
        {
            _surface?.Draw(false, sample, deltaT);
            HideOrbitalMarkers();
            DrawNavigation(navigation, snapshot, sample, deltaT, observer.Sky);
            return true;
        }
        HideNavigation();
        if (observer.Mode is not (CameraMode.LunarTerrain or CameraMode.MarsTerrain) && !_camera.Current)
            _camera.MakeCurrent();
        if (_surface is { } surface)
        {
            bool isSurface = observer.Mode == CameraMode.Surface;
            bool available = surface.Draw(isSurface, sample, deltaT, observer.EarthObserver, observer.Sky, observer.Selection);
            if (isSurface && available)
                return true;
        }

        if (observer.Mode is CameraMode.Mission or CameraMode.Timeline)
            return DrawMission(snapshot, observer.Mode == CameraMode.Timeline);
        if (observer.Mode == CameraMode.Probe)
            return DrawProbe(snapshot);
        if (observer.Mode == CameraMode.LunarTerrain)
        {
            HideOrbitalMarkers();
            if (_terrain is null || !snapshot.TryOrientation(SolarSystemSample.MoonId, out var moonFrame))
                return false;
            Vector3d sun = (sample.Sun - sample.Moon).PositionKilometers;
            return _terrain.Draw(observer.Terrain ?? LunarTerrainCamera.Globe, moonFrame.ToWorld.Transpose().Transform(sun));
        }

        if (observer.Mode == CameraMode.MarsTerrain)
        {
            HideOrbitalMarkers();
            if (_marsTerrain is null || !snapshot.TryOrientation(PlanetCatalogue.MarsId, out var frame) ||
                !snapshot.TryState(PlanetCatalogue.MarsId, out CartesianState mars))
                return false;
            Vector3d sun = (sample.Sun - mars).PositionKilometers;
            return _marsTerrain.Draw(new(-133.8, 18.65, 950), frame.ToWorld.Transpose().Transform(sun));
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
            Basis3d mesh = asset.Track.MeshToBody;
            float marker = asset.MarkerScale * (observer.Selection == asset.Track.Id ? 1f : 0.75f);
            Vector3 Column(Vector3d value) => ToGodot(body.Transform(value), 1) * marker;
            node.Transform = new Transform3D(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)),
                ToGodot(earthRelative.PositionKilometers, SatelliteKilometersPerUnit));
            node.Show();
        }
        return observer.Mode != CameraMode.Surface;
    }

    private bool DrawMission(WorldSnapshot snapshot, bool timeline)
    {
        HideOrbitalMarkers();

        _camera.Size = 210;
        PreparedMission? mission = timeline ? _timeline : _mission;
        if (mission is null)
            return false;
        SaturnSurfacePose pose = default;
        bool hasSurface = !timeline && mission.Surface?.TrySample(snapshot.Epoch, out pose) == true;
        _saturn.Visible = !timeline;
        if (hasSurface)
            DrawSaturn(pose);
        if (!snapshot.TryState(mission.Id, out var state) ||
            !snapshot.TryStateCenter(mission.Id, out ObjectId centerId) || !snapshot.TryState(centerId, out var center))
            return false;

        Vector3d observer = (state - center).PositionKilometers;
        double scale = MissionKilometersPerUnit;
        if (timeline)
        {
            scale = Math.Max(scale, observer.Length / 75);
            _saturn.Visible = centerId == SolarSystemSample.SaturnBarycenterId;
            _sun.Visible = centerId == SolarSystemSample.SunId;
            _sun.Position = Vector3.Zero;
        }
        Vector3 position = ToGodot(observer, scale);
        _camera.Size = Math.Max(210, 2.5f * position.Length());
        if (!snapshot.TryOrientation(mission.Id, out var rotation))
            return false;

        Basis3d body = rotation.ToWorld;
        var attitude = new Basis(ToGodot(body.X, 1), ToGodot(body.Y, 1), ToGodot(body.Z, 1));
        if (_missionNode.Mesh != mission.Mesh)
            _missionNode.Mesh = mission.Mesh;
        _missionNode.Transform = new(attitude.Scaled(Vector3.One * mission.MarkerScale), position);
        _missionNode.Show();
        _missionLabel.Position = position + new Vector3(0, 0, 9);
        _missionLabel.Show();
        if (hasSurface && mission.Frusta is not null)
            DrawProjectedFrusta(mission, pose, observer, body);
        else if (mission.Frusta is not null)
        {
            _missionFrusta.Transform = new(attitude, position);
            _missionFrusta.Show();
        }
        return true;
    }

    private bool DrawProbe(WorldSnapshot snapshot)
    {
        HideOrbitalMarkers();
        _camera.Size = 60;
        if (_probe is not { } probe || !snapshot.TryOrientation(probe.Track.Id, out var rotation) ||
            !snapshot.TryState(probe.Track.Id, out var state) ||
            !snapshot.TryStateCenter(probe.Track.Id, out var centerId) || !snapshot.TryState(centerId, out var center))
            return false;

        Basis3d body = rotation.ToWorld, mesh = probe.Track.MeshToBody;
        Vector3 Column(Vector3d value) => ToGodot(body.Transform(value), 1) * probe.MarkerScale;
        _missionNode.Mesh = probe.Mesh;
        _missionNode.Transform = new(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)), Vector3.Zero);
        _missionNode.Show();
        double distance = (state - center).PositionKilometers.Length;
        ProbeStatus = centerId == probe.Track.Carrier
            ? FormattableString.Invariant($"Attached to Cassini · {distance * 1000:F2} m")
            : snapshot.Epoch.CompareTo(probe.Track.Landing) < 0
                ? FormattableString.Invariant($"Free flight · {distance:F1} km from Titan center")
                : "Landed on Titan";
        return true;
    }

    private void HideOrbitalMarkers()
    {
        _sun.Hide();
        _earth.Hide();
        _moon.Hide();
        foreach (var (_, node) in _satellites.Values)
            node.Hide();
    }

    private void DrawSaturn(SaturnSurfacePose pose)
    {
        Basis3d matrix = pose.InertialToFixed;
        Vector3 x = ToGodot(new(matrix.X.X, matrix.Y.X, matrix.Z.X), 1);
        Vector3 y = ToGodot(new(matrix.X.Y, matrix.Y.Y, matrix.Z.Y), 1);
        Vector3 z = ToGodot(new(matrix.X.Z, matrix.Y.Z, matrix.Z.Z), 1);
        Vector3d radii = pose.RadiiKilometers / MissionKilometersPerUnit;
        _saturnSurface.Transform = new(new Basis(x * (float)radii.X, y * (float)radii.Y, z * (float)radii.Z),
            ToGodot(pose.CenterKilometers, MissionKilometersPerUnit));
        _saturn.Hide();
        _saturnSurface.Show();
    }

    private void DrawProjectedFrusta(PreparedMission mission, SaturnSurfacePose pose, Vector3d observer, Basis3d attitude)
    {
        const int EdgeSamples = 12;
        _projectionMesh.ClearSurfaces();
        _footprintMesh.ClearSurfaces();
        Vector3 apex = ToGodot(observer, MissionKilometersPerUnit);
        for (int index = 0; index < mission.Sensors.Count; index++)
        {
            SpiceSensor sensor = mission.Sensors[index];
            var surfaceSegments = new List<Vector3>();
            _projectionMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, mission.Frusta!.SurfaceGetMaterial(index));
            for (int edge = 0; edge < sensor.CornersInBody.Count; edge++)
            {
                Vector3d first = sensor.CornersInBody[edge];
                Vector3d second = sensor.CornersInBody[(edge + 1) % sensor.CornersInBody.Count];
                Vector3 previous = default;
                bool previousHit = false;
                for (int sample = 0; sample <= EdgeSamples; sample++)
                {
                    Vector3d ray = attitude.Transform(first + (second - first) * (sample / (double)EdgeSamples));
                    bool hit = pose.TryIntercept(observer, ray, sensor.RangeKilometers, out var endpoint);
                    if (hit)
                        endpoint = pose.CenterKilometers + (endpoint - pose.CenterKilometers) * 1.0005;
                    else
                        endpoint = observer + ray / ray.Length * sensor.RangeKilometers;
                    Vector3 point = ToGodot(endpoint, MissionKilometersPerUnit);
                    if (sample == 0)
                    {
                        _projectionMesh.SurfaceAddVertex(apex);
                        _projectionMesh.SurfaceAddVertex(point);
                    }
                    else if (hit && previousHit)
                    {
                        surfaceSegments.Add(previous);
                        surfaceSegments.Add(point);
                    }
                    else if (!hit && !previousHit)
                    {
                        _projectionMesh.SurfaceAddVertex(previous);
                        _projectionMesh.SurfaceAddVertex(point);
                    }
                    previous = point;
                    previousHit = hit;
                }
            }
            _projectionMesh.SurfaceEnd();
            if (surfaceSegments.Count > 0)
            {
                _footprintMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _footprintMaterials[index]);
                foreach (Vector3 point in surfaceSegments)
                    _footprintMesh.SurfaceAddVertex(point);
                _footprintMesh.SurfaceEnd();
            }
        }
        _projectedFrusta.Show();
        _footprints.Visible = _footprintMesh.GetSurfaceCount() > 0;
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
