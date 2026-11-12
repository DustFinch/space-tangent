// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const string ShipModel = "res://Models/space-shuttle.glb";
    private static readonly Vector3 ShipModelCenter = new(29.464966f, .0075073f, 138.036637f);
    private const double KilometersPerInch = .0000254;
    private Node3D? _shipModel;
    private MeshInstance3D? _shipTrail;
    private ImmediateMesh? _shipTrailMesh;

    internal static bool ShipModelAvailable => ResourceLoader.Exists(ShipModel);
    internal bool ShipHullVisible => _shipModel?.Visible == true;
    internal bool ShipTrailVisible => _shipTrail?.Visible == true;

    internal void DrawShip(ShipView? ship, NavigationCamera? camera)
    {
        if (ship is null || camera is not { } pose)
        {
            _shipModel?.Hide();
            _shipTrail?.Hide();
            return;
        }

        double units = pose.RangeKilometers / NavigationCameraUnits;
        Vector3 Project(Vector3d point) => ToGodot(point - pose.AnchorKilometers, units);
        if (!ship.Cockpit)
        {
            if (_shipModel is null && GD.Load<PackedScene>(ShipModel) is { } resource)
            {
                _shipModel = resource.Instantiate<Node3D>();
                AddChild(_shipModel);
            }
            if (_shipModel is { } hull)
            {
                var basis = new Basis(ToGodot(ship.Forward, 1), ToGodot(Vector3d.Cross(ship.Up, ship.Forward), 1),
                    ToGodot(ship.Up, 1)).Scaled(Vector3.One * (float)(KilometersPerInch / units));
                hull.Transform = new(basis, Project(ship.Position) - basis * ShipModelCenter);
                hull.Show();
            }
        }
        else
            _shipModel?.Hide();

        if (ship.Trail.Count < 2)
        {
            _shipTrail?.Hide();
            return;
        }
        if (_shipTrail is null)
        {
            _shipTrailMesh = new();
            _shipTrail = new MeshInstance3D
            {
                Mesh = _shipTrailMesh,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    VertexColorUseAsAlbedo = true
                }
            };
            AddChild(_shipTrail);
        }
        _shipTrailMesh!.ClearSurfaces();
        _shipTrailMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
        int index = 0;
        foreach (Vector3d point in ship.Trail)
        {
            Vector3 projected = Project(point);
            if (projected.LengthSquared() > NavigationFar * NavigationFar * .64f)
                projected = projected.Normalized() * (NavigationFar * .8f);
            _shipTrailMesh.SurfaceSetColor(new Color(1, .55f, .2f, (float)index++ / (ship.Trail.Count - 1)));
            _shipTrailMesh.SurfaceAddVertex(projected);
        }
        _shipTrailMesh.SurfaceEnd();
        _shipTrail.Show();
    }
}
