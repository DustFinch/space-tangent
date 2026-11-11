// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedSatellite(ScienceSatellite Track, ArrayMesh Mesh, float MarkerScale);

internal partial class SimulationViewport : Node3D
{
    private const double OverviewKilometersPerUnit = 1_000_000;
    private const double LunarKilometersPerUnit = 4_000;
    private const double SatelliteKilometersPerUnit = 75;

    private Camera3D _camera = null!;
    private Node3D _sun = null!, _earth = null!, _moon = null!;
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

    internal void Draw(ObserverView observer, SolarSystemSample sample)
    {
        ObjectId origin = observer.Mode == CameraMode.Solar ? SolarSystemSample.SunId : SolarSystemSample.EarthId;
        double scale = observer.Mode == CameraMode.Solar ? OverviewKilometersPerUnit : LunarKilometersPerUnit;
        _sun.Position = ToGodot(sample.RelativeState(SolarSystemSample.SunId, origin).PositionKilometers, scale);
        _earth.Position = ToGodot(sample.RelativeState(SolarSystemSample.EarthId, origin).PositionKilometers, scale);
        _moon.Position = ToGodot(sample.RelativeState(SolarSystemSample.MoonId, origin).PositionKilometers, scale);
        _sun.Visible = observer.Mode == CameraMode.Solar;
        _moon.Visible = observer.Mode == CameraMode.Lunar;
        _camera.Size = observer.Mode == CameraMode.Solar ? 365 : 250;

        foreach (var (asset, node) in _satellites.Values)
        {
            if (observer.Mode != CameraMode.Satellites || !asset.Track.TrySample(observer.Epoch, out var state))
            {
                node.Hide();
                continue;
            }

            Basis3d body = state.BodyToTrajectory;
            Basis3d mesh = state.MeshToBody;
            float marker = asset.MarkerScale * (observer.Selection == asset.Track.Id ? 1f : 0.75f);
            Vector3 Column(Vector3d value) => ToGodot(body.Transform(value), 1) * marker;
            node.Transform = new Transform3D(new Basis(Column(mesh.X), Column(mesh.Y), Column(mesh.Z)),
                ToGodot(state.EarthRelativeState.PositionKilometers, SatelliteKilometersPerUnit));
            node.Show();
        }
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
