// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

[Flags]
public enum BodyReferenceMarks
{
    None = 0, BodyAxes = 1, FrameAxes = 2, SunDirection = 4, Velocity = 8, Grid = 16, Terminator = 32,
    All = BodyAxes | FrameAxes | SunDirection | Velocity | Grid | Terminator
}

public readonly record struct BodyReferenceGeometry(Vector3d Center, double EquatorialRadius, double PolarRadius,
    Basis3d? Body, Vector3d? ToSun, Vector3d? RelativeVelocity)
{
    public Vector3d SurfacePoint(double longitude, double latitude)
    {
        var (sinLon, cosLon) = Math.SinCos(longitude);
        var (sinLat, cosLat) = Math.SinCos(latitude);
        Vector3d direction = new(cosLat * cosLon, cosLat * sinLon, sinLat);
        double inverseRadius = Math.Sqrt((direction.X * direction.X + direction.Y * direction.Y) /
            (EquatorialRadius * EquatorialRadius) + direction.Z * direction.Z / (PolarRadius * PolarRadius));
        return Body!.Value.Transform(direction / inverseRadius);
    }

    public Vector3d TerminatorPoint(double angle)
    {
        Basis3d body = Body!.Value;
        Vector3d sun = body.Transpose().Transform(ToSun!.Value);
        var m = Unit(new(sun.X / EquatorialRadius, sun.Y / EquatorialRadius, sun.Z / PolarRadius));
        Vector3d first = Unit(Vector3d.Cross(m, Math.Abs(m.Z) < .9 ? new(0, 0, 1) : new(1, 0, 0)));
        Vector3d second = Vector3d.Cross(m, first);
        var (sin, cos) = Math.SinCos(angle);
        Vector3d unit = first * cos + second * sin;
        return body.Transform(new(unit.X * EquatorialRadius, unit.Y * EquatorialRadius, unit.Z * PolarRadius));
    }

    private static Vector3d Unit(Vector3d value) => value / value.Length;
}

public sealed partial class SimulationSession
{
    public void SetReferenceMarks(int view, BodyReferenceMarks marks)
    {
        if ((marks & ~BodyReferenceMarks.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(marks));
        _views[view] = _views[view] with { ReferenceMarks = marks };
    }

    public BodyReferenceGeometry? ReferenceGeometry(int view, WorldSnapshot snapshot)
    {
        ObserverView observer = _views[view];
        if (observer.Navigation is null || observer.ReferenceMarks == BodyReferenceMarks.None ||
            observer.Selection is not { } id || World.Catalogue.Find(id) is not { } selected ||
            selected.Kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.Asteroid or
                CatalogueKind.Comet or CatalogueKind.DwarfPlanet or CatalogueKind.Spacecraft) ||
            !snapshot.TryState(id, out var state))
            return null;
        double equatorial = World.TargetRadiusKilometers(id);
        double polar = PlanetCatalogue.Find(id)?.PolarRadiusKilometers ?? equatorial;
        Basis3d? body = NavigationCamera.TryBodyBasis(snapshot, id, DeltaT, out var frame) ? frame : null;
        Vector3d? sun = snapshot.SolarSystem is { } solar ? Unit((solar.Sun - state).PositionKilometers) : null;
        Vector3d? velocity = selected.Parent is { } parent && snapshot.TryState(parent, out var origin)
            ? Unit((state - origin).VelocityKilometersPerSecond) : null;
        return new(state.PositionKilometers, equatorial, polar, body, sun, velocity);

        static Vector3d? Unit(Vector3d direction) => direction.Length > 0 ? direction / direction.Length : null;
    }
}
