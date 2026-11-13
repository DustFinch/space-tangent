// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly List<ArchivedComet> _archivedComets = [];
    internal int ArchivedCometCount => _archivedComets.Count(comet => comet.Visible);

    private void DrawArchivedComets(IReadOnlyList<RecordedCometTail> tails, double scale)
    {
        Basis basis = _navigationCamera.Basis;
        Vector3 eye = _navigationCamera.Position;
        for (int index = 0; index < tails.Count; index++)
        {
            if (_archivedComets.Count == index)
                _archivedComets.Add(new(this));
            _archivedComets[index].Draw(tails[index], scale, basis, eye, Project);
        }

        (Vector3 Position, double Scale) Project(Vector3d point)
        {
            double distance = point.Length * scale;
            double projection = distance > NavigationFar * .8 ? NavigationFar * .8 / distance : 1;
            Vector3 local = Column(point * (projection * scale));
            return (eye + basis * local, projection);
        }
    }

    internal void HideArchivedComets()
    {
        foreach (var comet in _archivedComets)
            comet.Hide();
    }

    private static Vector3 Column(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);

    private sealed class ArchivedComet
    {
        private readonly CometVisual.CometRibbon _ribbon;
        private readonly ShaderMaterial _tailMaterial, _comaMaterial;
        private readonly MeshInstance3D _coma;

        internal ArchivedComet(Node parent)
        {
            var shader = GD.Load<Shader>("res://Shaders/comet_tail.gdshader");
            _tailMaterial = CometVisual.Material(shader, Vector3.One, false);
            _tailMaterial.SetShaderParameter("recorded_profile", true);
            _comaMaterial = CometVisual.Material(shader, Vector3.One, true);
            _ribbon = new(parent, _tailMaterial);
            _coma = CometVisual.MakeNode(parent, new QuadMesh { Size = new(2, 2) }, _comaMaterial);
        }

        internal bool Visible => _ribbon.Visible;

        internal void Draw(RecordedCometTail tail, double scale, Basis basis, Vector3 eye,
            Func<Vector3d, (Vector3 Position, double Scale)> project)
        {
            var tint = Column(tail.Color);
            float brightness = (float)tail.Brightness;
            _tailMaterial.SetShaderParameter("tint", tint);
            _ribbon.Draw(tail.Head, tail.Direction, tail.Trail, tail.Length, tail.Width,
                brightness, 1 / scale, eye, project, basis);
            if (tail.ComaRadius <= 0)
                return;
            float fade = Mathf.SmoothStep(.5f, 2f, (float)(tail.Head.Length / tail.ComaRadius));
            if (fade <= 0)
                return;
            var (center, projection) = project(tail.Head);
            float radius = (float)(tail.ComaRadius * scale * projection);
            _comaMaterial.SetShaderParameter("tint", tint);
            _comaMaterial.SetShaderParameter("brightness", brightness * fade);
            _coma.Transform = new(basis.Scaled(Vector3.One * radius), center);
            _coma.Show();
        }

        internal void Hide()
        {
            _ribbon.Hide();
            _coma.Hide();
        }
    }
}
