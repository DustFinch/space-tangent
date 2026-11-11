// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedSatellite(ScienceSatellite Track, ArrayMesh Mesh, float MarkerScale);
internal sealed record PreparedMission(ObjectId Id, ArrayMesh Mesh, float MarkerScale);

internal partial class SimulationViewport : Node3D
{
    private const double OverviewKilometersPerUnit = 1_000_000;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SatelliteKilometersPerUnit = 75;
    private const double MissionKilometersPerUnit = 2500;

    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!, _saturn = null!;
    private PreparedMission? _mission;
    private MeshInstance3D _missionNode = null!;
    private SurfaceProjection? _surface;
    private readonly Dictionary<ObjectId, (PreparedSatellite Asset, MeshInstance3D Node)> _satellites = new();

    internal Vector3 EarthPosition => _earth.Position;
    internal Vector3 MoonPosition => _moon.Position;
    internal bool MoonVisible => _moon.Visible;

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
        _saturn = CreateMarker("Saturn barycenter", new Color("#d9bb7f"), 9);
        _missionNode = new MeshInstance3D { Visible = false };
        AddChild(_missionNode);
    }

    private void FitCamera()
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        _camera.KeepAspect = size.X >= size.Y ? Camera3D.KeepAspectEnum.Height : Camera3D.KeepAspectEnum.Width;
    }

    internal void ReplaceSatellites(IReadOnlyList<PreparedSatellite> assets)
    {
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

    internal void ReplaceMission(PreparedMission? asset)
    {
        _mission = asset;
        _missionNode.Mesh = asset?.Mesh;
        _missionNode.Hide();
    }

    internal void SetSurfaceTextures(SurfaceTextures textures) => _surface = new SurfaceProjection(this, textures);

    internal void SetSurfaceControls(bool clouds, bool lunarLight) => _surface!.SetControls(clouds, lunarLight);

    internal bool Draw(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        SolarSystemSample sample = snapshot.SolarSystem ?? throw new InvalidOperationException("The view needs a solar-system snapshot.");
        _saturn.Visible = observer.Mode == CameraMode.Mission;
        _missionNode.Hide();
        if (_surface is { } surface)
        {
            bool isSurface = observer.Mode == CameraMode.Surface;
            bool available = surface.Draw(isSurface, sample, deltaT);
            if (isSurface && available)
                return true;
        }

        if (observer.Mode == CameraMode.Mission)
            return DrawMission(snapshot);

        ObjectId origin = observer.Mode == CameraMode.Solar ? SolarSystemSample.SunId : SolarSystemSample.EarthId;
        double scale = observer.Mode == CameraMode.Solar ? OverviewKilometersPerUnit : LunarKilometersPerUnit;
        _sun.Position = ToGodot(sample.RelativeState(SolarSystemSample.SunId, origin).PositionKilometers, scale);
        _earth.Position = ToGodot(sample.RelativeState(SolarSystemSample.EarthId, origin).PositionKilometers, scale);
        _moon.Position = ToGodot(sample.RelativeState(SolarSystemSample.MoonId, origin).PositionKilometers, scale);
        _earth.Show();
        _sun.Visible = observer.Mode == CameraMode.Solar;
        _moon.Visible = observer.Mode == CameraMode.Lunar;
        _camera.Size = observer.Mode == CameraMode.Solar ? 365 : 250;

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

    private bool DrawMission(WorldSnapshot snapshot)
    {
        _sun.Hide();
        _earth.Hide();
        _moon.Hide();
        foreach (var (_, node) in _satellites.Values)
            node.Hide();

        _camera.Size = 210;
        if (_mission is not { } mission || !snapshot.TryState(mission.Id, out var state) ||
            !snapshot.TryState(SolarSystemSample.SaturnBarycenterId, out var center))
            return false;

        Vector3 position = ToGodot((state - center).PositionKilometers, MissionKilometersPerUnit);
        _camera.Size = Math.Max(210, 2.5f * position.Length());
        if (!snapshot.TryOrientation(mission.Id, out var rotation))
            return false;

        Basis3d body = rotation.ToWorld;
        Vector3 Column(Vector3d axis) => ToGodot(axis, 1) * mission.MarkerScale;
        _missionNode.Transform = new(new Basis(Column(body.X), Column(body.Y), Column(body.Z)), position);
        _missionNode.Show();
        return true;
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
}
