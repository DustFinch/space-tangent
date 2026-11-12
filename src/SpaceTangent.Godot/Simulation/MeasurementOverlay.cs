// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class MeasurementOverlay : Control
{
    private static readonly Color Line = new(1f, .83f, .39f, .94f);
    private Camera3D? _camera;
    private Vector3? _angleFrom, _angleTo;
    private bool _dragging;
    private ObjectId? _distanceFrom, _distanceTo;
    private (Vector2 From, Vector2 To)? _distancePoints;
    private string _distanceText = "";

    internal MeasurementOverlay()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    internal bool AngleEnabled { get; private set; }
    internal double? AngleDegrees => _angleFrom is { } from && _angleTo is { } to
        ? Math.Atan2(from.Cross(to).Length(), from.Dot(to)) * 180 / Math.PI : null;
    internal double? DistanceKilometers { get; private set; }
    internal const double LightKilometersPerSecond = 299_792.458;

    internal bool ToggleAngle()
    {
        AngleEnabled = !AngleEnabled;
        if (!AngleEnabled)
        {
            _angleFrom = _angleTo = null;
            _dragging = false;
        }
        QueueRedraw();
        return AngleEnabled;
    }

    internal bool HandleAngleInput(InputEvent input)
    {
        if (!AngleEnabled || _camera is not { } camera)
            return false;
        switch (input)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _angleFrom = _angleTo = camera.ProjectRayNormal(press.Position);
                _dragging = true;
                break;
            case InputEventMouseMotion motion when _dragging:
                _angleTo = camera.ProjectRayNormal(motion.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } release when _dragging:
                _angleTo = camera.ProjectRayNormal(release.Position);
                _dragging = false;
                break;
            default:
                return false;
        }
        QueueRedraw();
        return true;
    }

    internal void SelectDistancePoint(ObjectId target)
    {
        if (_distanceFrom is null || _distanceTo is not null)
        {
            _distanceFrom = target;
            _distanceTo = null;
        }
        else if (_distanceFrom != target)
            _distanceTo = target;
    }

    internal bool WaitingForSecondPoint => _distanceFrom is not null && _distanceTo is null;

    internal void ClearDistance()
    {
        _distanceFrom = _distanceTo = null;
        _distancePoints = null;
        DistanceKilometers = null;
        QueueRedraw();
    }

    internal void Update(SimulationViewport renderer, ObserverView observer, WorldSnapshot snapshot,
        SimulationWorld world, DeltaTModel deltaT)
    {
        _camera = observer.Navigation.HasValue ? renderer.NavigationCameraNode : null;
        _distancePoints = null;
        DistanceKilometers = null;
        if (_distanceFrom is { } from && _distanceTo is { } to &&
            world.TryNavigationState(from, snapshot, out var first, deltaT) &&
            world.TryNavigationState(to, snapshot, out var second, deltaT))
        {
            double distance = (second - first).PositionKilometers.Length;
            DistanceKilometers = distance;
            _distanceText = FormattableString.Invariant(
                $"{world.Catalogue.Find(from)?.Name} – {world.Catalogue.Find(to)?.Name}   {distance:N1} km · light {distance / LightKilometersPerSecond:G4} s");
            if (observer.Navigation is { } pose &&
                renderer.ProjectNavigationPoint(pose, first.PositionKilometers) is { } a &&
                renderer.ProjectNavigationPoint(pose, second.PositionKilometers) is { } b)
                _distancePoints = (a, b);
        }
        if (_angleFrom is not null || _distanceFrom is not null)
            QueueRedraw();
    }

    internal string DistanceText => DistanceKilometers.HasValue ? _distanceText : "";

    public override void _Draw()
    {
        if (_distancePoints is { } points)
        {
            DrawDashedLine(points.From, points.To, Line, 1.5f, 7, true, true);
            DrawCircle(points.From, 3, Line);
            DrawCircle(points.To, 3, Line);
            DrawString(ThemeDB.FallbackFont, (points.From + points.To) / 2 + new Vector2(8, -8),
                _distanceText, HorizontalAlignment.Left, -1, 15, Line);
        }
        if (_camera is not { } camera || _angleFrom is not { } from || _angleTo is not { } to ||
            AngleDegrees is not { } degrees)
            return;
        Vector2? previous = null;
        const int steps = 48;
        for (int step = 0; step <= steps; step++)
        {
            Vector3 direction = from.Slerp(to, step / (float)steps);
            Vector3 position = camera.GlobalPosition + direction * 10_000;
            Vector2? point = camera.IsPositionBehind(position) ? null : camera.UnprojectPosition(position);
            if (point is { } next && previous is { } last)
                DrawLine(last, next, Line, 1.5f, true);
            if (point is { } end && (step == 0 || step == steps))
                DrawCircle(end, 3, Line);
            if (point is { } middle && step == steps / 2)
                DrawString(ThemeDB.FallbackFont, middle + new Vector2(8, -8), FormatAngle(degrees),
                    HorizontalAlignment.Left, -1, 15, Line);
            previous = point;
        }
    }

    internal static string FormatAngle(double degrees)
    {
        int seconds = (int)Math.Round(degrees * 3600);
        return $"{seconds / 3600}° {seconds % 3600 / 60:00}′ {seconds % 60:00}″";
    }
}
