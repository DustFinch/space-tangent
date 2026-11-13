// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private Node3D? _archivedOverlayRoot;
    private readonly ImmediateMesh _archivedOrbitMesh = new();
    private RecordedSceneGeometry? _lastArchivedOverlays;
    private double _lastArchivedScale;
    private readonly List<Label3D> _archivedLabels = [];
    private readonly StandardMaterial3D _archivedOrbitMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true
    };
    internal int ArchivedOrbitSurfaces => _archivedOverlayRoot is { Visible: true } ? _archivedOrbitMesh.GetSurfaceCount() : 0;
    internal IReadOnlyList<Label3D> ArchivedLabels => _archivedOverlayRoot is { Visible: true } ? _archivedLabels : [];

    private void DrawArchivedOverlays(RecordedSceneGeometry scene, double scale)
    {
        if (scene.Orbits.Count == 0 && scene.Labels.Count == 0)
            return;
        if (_archivedOverlayRoot is null)
        {
            _archivedOverlayRoot = new();
            AddChild(_archivedOverlayRoot);
            _archivedOverlayRoot.AddChild(new MeshInstance3D
            {
                Mesh = _archivedOrbitMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
        }

        _archivedOverlayRoot.Transform = _navigationCamera.Transform;
        _archivedOverlayRoot.Show();
        if (ReferenceEquals(_lastArchivedOverlays, scene) && _lastArchivedScale == scale)
            return;
        _lastArchivedOverlays = scene;
        _lastArchivedScale = scale;
        _archivedOrbitMesh.ClearSurfaces();
        foreach (var orbit in scene.Orbits)
        {
            _archivedOrbitMesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip, _archivedOrbitMaterial);
            Vector3d color = orbit.Color ?? new Vector3d(.44, .53, .63);
            for (int index = 0; index < orbit.Points.Count; index++)
            {
                double brightness = 1 - orbit.Fade * (1 - index / (double)(orbit.Points.Count - 1));
                _archivedOrbitMesh.SurfaceSetColor(new((float)(color.X * brightness),
                    (float)(color.Y * brightness), (float)(color.Z * brightness)));
                _archivedOrbitMesh.SurfaceAddVertex(Project(orbit.CameraPoint(index)));
            }
            _archivedOrbitMesh.SurfaceEnd();
        }

        for (int index = 0; index < scene.Labels.Count; index++)
        {
            if (_archivedLabels.Count == index)
            {
                var label = CreateLabel("", Vector3.Zero);
                label.FixedSize = true;
                label.PixelSize = .0019f;
                label.FontSize = 14;
                label.HorizontalAlignment = HorizontalAlignment.Left;
                _archivedOverlayRoot.AddChild(label);
                _archivedLabels.Add(label);
            }
            var record = scene.Labels[index];
            var node = _archivedLabels[index];
            node.Text = record.Text;
            node.Position = Project(record.CameraPosition);
            Vector3d rgb = record.Color ?? DefaultLabelColor(record.Kind);
            node.Modulate = new((float)rgb.X, (float)rgb.Y, (float)rgb.Z, (float)record.Opacity);
            node.Visible = record.CameraPosition.Z < 0 && record.Opacity > 0;
        }
        for (int index = scene.Labels.Count; index < _archivedLabels.Count; index++)
            _archivedLabels[index].Hide();

        Vector3 Project(Vector3d point)
        {
            double distance = point.Length * scale;
            double factor = distance > NavigationFar * .8 ? NavigationFar * .8 / point.Length : scale;
            return new((float)(point.X * factor), (float)(point.Y * factor), (float)(point.Z * factor));
        }
    }

    private static Vector3d DefaultLabelColor(int kind) => kind switch
    {
        0 => new(.471, .356, .682),
        2 => new(.231, .733, .792),
        3 => new(.225, .301, .36),
        4 => new(0, .45, .5),
        5 => new(.8, .45, .5),
        6 => new(.75, .6, 1),
        7 => new(.24, .89, .43),
        8 => new(.4, .6, .95),
        9 => new(.541, .764, .278),
        _ => new(.407, .333, .964)
    };
}
