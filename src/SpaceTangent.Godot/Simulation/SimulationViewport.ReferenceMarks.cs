// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private ImmediateMesh? _referenceLines;
    private MeshInstance3D? _referenceMarks;
    internal bool ReferenceMarksVisible => _referenceMarks?.Visible == true;

    internal void DrawReferenceMarks(ObserverView view, BodyReferenceGeometry? sample)
    {
        _referenceMarks?.Hide();
        if (view.Navigation is not { } pose || sample is not { } geometry)
            return;
        var flags = view.ReferenceMarks;
        if (!geometry.Body.HasValue)
            flags &= ~(BodyReferenceMarks.BodyAxes | BodyReferenceMarks.Grid | BodyReferenceMarks.Terminator);
        if (!geometry.ToSun.HasValue)
            flags &= ~(BodyReferenceMarks.SunDirection | BodyReferenceMarks.Terminator);
        if (!geometry.RelativeVelocity.HasValue)
            flags &= ~BodyReferenceMarks.Velocity;
        if (flags == BodyReferenceMarks.None)
            return;
        double units = _navigationUnits;
        Vector3 center = ProjectNavigation((geometry.Center - pose.AnchorKilometers) - _navigationEyeOffset, units, out double projection);
        double scale = projection / units;
        if (geometry.EquatorialRadius * scale < .03)
            return;
        if (_referenceLines is null)
        {
            _referenceLines = new ImmediateMesh();
            _referenceMarks = new MeshInstance3D
            {
                Mesh = _referenceLines,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(_referenceMarks);
        }

        _referenceLines.ClearSurfaces();
        _referenceLines.SurfaceBegin(Mesh.PrimitiveType.Lines);
        if ((flags & BodyReferenceMarks.BodyAxes) != 0 && geometry.Body is { } body)
            Axes(body, new("f37e78"), new("81d29a"), new("83b7ff"));
        if ((flags & BodyReferenceMarks.FrameAxes) != 0)
        {
            var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
            Axes(new(new(1, 0, 0), new(0, cos, sin), new(0, -sin, cos)), new("ffd2ba"), new("bee8ce"), new("b9d6f3"), 1.65);
        }
        if ((flags & BodyReferenceMarks.SunDirection) != 0 && geometry.ToSun is { } sun)
            Arrow(sun, new("ffdb88"), 1.85);
        if ((flags & BodyReferenceMarks.Velocity) != 0 && geometry.RelativeVelocity is { } velocity)
            Arrow(velocity, new("baf3ec"), 1.76);
        if ((flags & BodyReferenceMarks.Grid) != 0 && geometry.Body.HasValue)
        {
            var color = new Color(.63f, .79f, 1f, .43f);
            for (int longitude = 0; longitude < 12; longitude++)
            {
                double angle = longitude * Math.Tau / 12;
                Curve(t => geometry.SurfacePoint(angle, (t - .5) * Math.PI), color);
            }
            for (int latitude = -2; latitude <= 2; latitude++)
            {
                double angle = latitude * Math.PI / 6;
                Curve(t => geometry.SurfacePoint(t * Math.Tau, angle), color);
            }
        }
        if ((flags & BodyReferenceMarks.Terminator) != 0 && geometry.Body.HasValue && geometry.ToSun.HasValue)
            Curve(t => geometry.TerminatorPoint(t * Math.Tau), new("edb77b"));
        _referenceLines.SurfaceEnd();
        _referenceMarks!.Show();

        void Axes(Basis3d frame, Color x, Color y, Color z, double length = 1.48)
        {
            Arrow(frame.X, x, length);
            Arrow(frame.Y, y, length);
            Arrow(frame.Z, z, length);
        }

        void Arrow(Vector3d direction, Color color, double length)
        {
            double radius = geometry.EquatorialRadius;
            Vector3d end = direction * (radius * length);
            Line(direction * (radius * .96), end, color);
            Vector3d side = Vector3d.Cross(direction, new(0, 0, 1));
            if (side.Length < .1)
                side = Vector3d.Cross(direction, new(0, 1, 0));
            side *= radius * .043 / side.Length;
            Vector3d back = end - direction * (radius * .11);
            Line(back + side, end, color);
            Line(back - side, end, color);
        }

        void Curve(Func<double, Vector3d> point, Color color)
        {
            const int steps = 96;
            Vector3d previous = point(0) * 1.005;
            for (int step = 1; step <= steps; step++)
            {
                Vector3d next = point((double)step / steps) * 1.005;
                Line(previous, next, color);
                previous = next;
            }
        }

        void Line(Vector3d start, Vector3d end, Color color)
        {
            _referenceLines.SurfaceSetColor(color);
            _referenceLines.SurfaceAddVertex(center + ToGodot(start * scale, 1));
            _referenceLines.SurfaceAddVertex(center + ToGodot(end * scale, 1));
        }
    }
}
