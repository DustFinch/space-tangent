// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class MissionCameraRendering
{
    internal const double KilometersPerUnit = 2500;
    private const double MissionKilometersPerUnit = KilometersPerUnit;
    private readonly Node3D _parent, _sun, _saturn;
    private readonly Camera3D _camera;
    private readonly Action _hideOrbitalMarkers;
    private readonly Func<Vector3d, double, StandardMaterial3D> _lineMaterial;
    internal string? ProbeStatus { get; private set; }
    private PreparedMission? _mission;
    private PreparedMission? _timeline;
    private PreparedProbe? _probe;
    private MeshInstance3D _missionNode = null!, _missionFrusta = null!;
    private Label3D _missionLabel = null!;
    private MeshInstance3D _saturnSurface = null!, _projectedFrusta = null!, _footprints = null!;
    private ImmediateMesh _projectionMesh = new(), _footprintMesh = new();
    private StandardMaterial3D[] _footprintMaterials = [];

    internal MissionCameraRendering(Node3D parent, Camera3D camera, Node3D sun, Node3D saturn,
        Action hideOrbitalMarkers, Func<string, Vector3, Label3D> createLabel,
        Func<Vector3d, double, StandardMaterial3D> lineMaterial)
    {
        _parent = parent; _camera = camera; _sun = sun; _saturn = saturn;
        _hideOrbitalMarkers = hideOrbitalMarkers; _lineMaterial = lineMaterial;
        _missionNode = new MeshInstance3D { Visible = false };
        _parent.AddChild(_missionNode);
        _missionLabel = createLabel("Cassini", Vector3.Zero);
        _missionLabel.Hide();
        _parent.AddChild(_missionLabel);
        _missionFrusta = new MeshInstance3D { Visible = false };
        _parent.AddChild(_missionFrusta);
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
        _parent.AddChild(_saturnSurface);
        _parent.AddChild(_projectedFrusta);
        _parent.AddChild(_footprints);
    }

    internal void Hide()
    {
        if (_closed) return;
        _missionNode.Hide();
        _missionLabel.Hide();
        _missionFrusta.Hide();
        _saturnSurface.Hide();
        _projectedFrusta.Hide();
        _footprints.Hide();
        ProbeStatus = null;
    }

    private bool _closed;
    internal void Close()
    {
        if (_closed) return;
        Hide();
        _closed = true;
        foreach (var material in _footprintMaterials) material.Dispose();
        _footprintMaterials = [];
        _projectedFrusta.Mesh = _footprints.Mesh = null;
        _projectionMesh.Dispose(); _footprintMesh.Dispose();
        _projectionMesh = _footprintMesh = null!;
        _saturnSurface.Mesh?.Dispose(); _saturnSurface.MaterialOverride?.Dispose();
        _saturnSurface.Mesh = null; _saturnSurface.MaterialOverride = null;
        _missionNode.Mesh = _missionFrusta.Mesh = null;
        _mission = _timeline = null; _probe = null;
    }

    internal MeshInstance3D MissionMesh => _missionNode;
    internal bool MissionUsesSaturn => _saturn.Visible || _saturnSurface.Visible;
    internal MeshInstance3D MissionFrusta => _missionFrusta;
    internal MeshInstance3D MissionProjectedFrusta => _projectedFrusta;
    internal bool MissionFootprintVisible => _footprints.Visible;
    internal bool MissionProjectionVisible => _projectedFrusta.Visible;
    internal int MissionFootprintCount => _closed ? 0 : _footprintMesh.GetSurfaceCount();

    internal void ReplaceMission(PreparedMission? asset, PreparedMission? timeline, PreparedProbe? probe)
    {
        if (_closed) return;
        if (ReferenceEquals(_mission, asset) && ReferenceEquals(_timeline, timeline) && ReferenceEquals(_probe, probe))
            return;
        _mission = asset;
        _timeline = timeline;
        _probe = probe;
        _missionNode.Hide();
        _missionLabel.Hide();
        _missionFrusta.Mesh = asset?.Frusta;
        _missionFrusta.Hide();
        foreach (var material in _footprintMaterials) material.Dispose();
        _footprintMaterials = asset?.Sensors.Select(sensor => _lineMaterial(sensor.Color, sensor.FootprintOpacity)).ToArray() ?? [];
    }

    internal bool DrawMission(WorldSnapshot snapshot, bool timeline)
    {
        if (_closed) return false;
        _hideOrbitalMarkers();

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

    internal bool DrawProbe(WorldSnapshot snapshot)
    {
        if (_closed) return false;
        _hideOrbitalMarkers();
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
}
