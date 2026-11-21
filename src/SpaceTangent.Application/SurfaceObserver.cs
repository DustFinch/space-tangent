// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Core.Surfaces;

namespace SpaceTangent.Application;

public static class SurfaceObserver
{
    public static IReadOnlyList<CatalogueObject> Bodies(SimulationWorld world) => world.Catalogue.Objects
        .Where(body => body.Kind is (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.DwarfPlanet or CatalogueKind.Asteroid) &&
            world.TargetRadiusKilometers(body.Id) >= 150 && DemoWorlds.Find(body.Id)?.Surface != DemoWorldSurface.GasGiant)
        .ToArray();

    public static bool TryFrame(SimulationWorld world, EarthObserverCamera camera, WorldSnapshot snapshot,
        DeltaTModel model, out BodySurfaceFrame frame)
    {
        frame = default;
        if (!camera.IsValid() || snapshot.SolarSystem is not { } solar) return false;
        if (camera.IsEarth)
        {
            var light = EarthSurfaceLighting.Sample(solar, camera.Site, model);
            var spin = EarthOrientation.TerrestrialToWorld(snapshot.Epoch, model).AngularVelocityRadiansPerSecond.GetValueOrDefault();
            frame = new(solar.Earth + new CartesianState(light.PositionKilometers, Vector3d.Cross(spin, light.PositionKilometers)), light.EastNorthUp);
            return true;
        }
        ObjectId id = camera.SurfaceBody;
        if (world.Catalogue.Find(id)?.Kind is not (CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.DwarfPlanet or CatalogueKind.Asteroid) ||
            !world.HasSurfaceOrientation(id) ||
            DemoWorlds.Find(id)?.Surface == DemoWorldSurface.GasGiant ||
            !snapshot.TryState(id, out var body) || !snapshot.TryOrientation(id, out var rotation)) return false;
        double radius = world.TargetRadiusKilometers(id);
        if (radius < 150) return false;
        double polar = PlanetCatalogue.Find(id)?.PolarRadiusKilometers ?? DemoWorlds.Find(id)?.PolarRadiusKilometers ?? radius;
        frame = BodySurfaceFrame.OnEllipsoid(body, rotation, camera.Site, radius, polar);
        return true;
    }

    /// <summary>A transient render pose; the saved observer remains a surface camera with its own body and site.</summary>
    public static bool TryNavigation(SimulationWorld world, EarthObserverCamera camera, WorldSnapshot snapshot,
        DeltaTModel model, out NavigationCamera navigation)
    {
        navigation = default;
        // A reference ellipsoid has no below-datum terrain: render above it while calculations retain the exact site.
        if (!TryFrame(world, camera with { Site = camera.Site with { HeightKilometers = Math.Min(100, Math.Max(0, camera.Site.HeightKilometers) + .003) } },
            snapshot, model, out var frame) || !snapshot.TryState(camera.SurfaceBody, out var body)) return false;
        var offset = frame.Observer.PositionKilometers - body.PositionKilometers;
        if (DemoWorlds.Find(camera.SurfaceBody) is { } demo && DemoWorlds.CreateSurface(demo) is { HasSurface: true } surface &&
            snapshot.TryOrientation(camera.SurfaceBody, out var axes))
        {
            var local = axes.ToWorld.Transpose().Transform(offset);
            var terrain = new ProceduralTerrain(surface, demo.PolarRadiusKilometers);
            var native = new Vector3d(local.X, local.Z, -local.Y) / local.Length;
            double lift = terrain.GroundRadiusKilometers(native) - local.Length + .003;
            if (lift > 0) offset += frame.EastNorthUp.Z * lift;
        }
        var forward = frame.LookDirection(camera.AzimuthDegrees, camera.ElevationDegrees);
        var up = frame.LookDirection(camera.AzimuthDegrees, camera.ElevationDegrees + 90);
        if (camera.EquatorialMount && snapshot.TryOrientation(camera.SurfaceBody, out var rotation))
        {
            var pole = rotation.ToWorld.Z * (camera.Site.LatitudeDegrees < 0 ? -1 : 1);
            var projected = pole - forward * Vector3d.Dot(pole, forward);
            if (projected.Length > .01) up = projected / projected.Length;
        }
        navigation = new(body.PositionKilometers, offset.Length, 0, 0, camera.SurfaceBody, true)
            { Placement = new(offset, forward, up) };
        return navigation.IsValid();
    }

    internal static void RequireCalculationFrame(SimulationWorld world, EarthObserverCamera camera, JulianInstant epoch, DeltaTModel model)
    {
        if (!TryFrame(world, camera, world.AlmanacGraph.Evaluate(epoch, model), model, out _))
            throw new ArgumentException("The observing body's position and declared orientation are unavailable for background calculations. Scripted bodies and their dependent frames are excluded.", nameof(camera));
    }
}
