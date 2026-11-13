// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public static partial class LegacyObserverProfiles
{
    public static ObserverView RestoreSurfaceBody(LegacyObserverProfile profile, SimulationWorld world)
    {
        var observer = profile.Observer;
        if (profile.SiteBody is not { } path || observer.EarthObserver is not { } camera ||
            (observer.Mode != CameraMode.Surface && observer.Navigation is not { Placement.BodyFixed: true }))
            return observer;
        if (path.Equals("Sol/Earth", StringComparison.OrdinalIgnoreCase))
            return observer with { Mode = CameraMode.Surface, Navigation = null };
        ObjectId body;
        try { body = SceneLink.ResolveObject(path, world.Catalogue); }
        catch (InvalidDataException) { return observer; }
        if (body == SolarSystemSample.EarthId)
            return observer with { Mode = CameraMode.Surface, Navigation = null };
        if (DemoWorlds.Find(body) is { Surface: DemoWorldSurface.GasGiant })
            return observer;
        if (world.Catalogue.Find(body)?.Kind is not (CatalogueKind.Planet or CatalogueKind.DwarfPlanet or CatalogueKind.Moon
            or CatalogueKind.Asteroid or CatalogueKind.Comet))
            return observer;
        var snapshot = world.Graph.Evaluate(observer.Epoch);
        if (!world.TryNavigationState(body, snapshot, out var state, profile.DeltaT) ||
            !NavigationCamera.TryBodyBasis(snapshot, body, profile.DeltaT, out _))
            return observer;

        double radius = world.TargetRadiusKilometers(body);
        double polar = PlanetCatalogue.Find(body)?.PolarRadiusKilometers ?? DemoWorlds.Find(body)?.PolarRadiusKilometers ?? radius;
        const double degrees = Math.PI / 180;
        var (sinLat, cosLat) = Math.SinCos(camera.Site.LatitudeDegrees * degrees);
        var (sinLon, cosLon) = Math.SinCos(camera.Site.LongitudeDegrees * degrees);
        Vector3d radial = new(cosLat * cosLon, cosLat * sinLon, sinLat);
        Vector3d east = new(-sinLon, cosLon, 0);
        Vector3d north = Vector3d.Cross(radial, east);
        double prime = radius * radius / Math.Sqrt(radius * radius * cosLat * cosLat + polar * polar * sinLat * sinLat);
        var ground = new Vector3d(prime * radial.X, prime * radial.Y, prime * polar * polar / (radius * radius) * sinLat);
        double height = camera.Site.HeightKilometers;
        if (DemoWorlds.Find(body) is { } demo && DemoWorlds.CreateSurface(demo) is { HasSurface: true } surface)
        {
            var terrain = new ProceduralTerrain(surface, polar);
            Vector3d local = ground / ground.Length;
            var native = new Vector3d(local.X, local.Z, -local.Y);
            height = Math.Max(height, terrain.GroundRadiusKilometers(native) - ground.Length);
        }
        var offset = ground + radial * (height + .003);
        var (sinAz, cosAz) = Math.SinCos(camera.AzimuthDegrees * degrees);
        var (sinAlt, cosAlt) = Math.SinCos(camera.ElevationDegrees * degrees);
        var horizontal = north * cosAz + east * sinAz;
        var forward = horizontal * cosAlt + radial * sinAlt;
        var up = radial * cosAlt - horizontal * sinAlt;
        if (camera.EquatorialMount)
        {
            var pole = new Vector3d(0, 0, sinLat < 0 ? -1 : 1);
            var projected = pole - forward * Vector3d.Dot(pole, forward);
            if (projected.Length > .01)
                up = projected / projected.Length;
        }
        var pose = new NavigationCamera(state.PositionKilometers, offset.Length, 0, 0, body, true)
        {
            Placement = new(offset, forward, up, BodyFixed: true)
        };
        return pose.IsValid() ? observer with { Mode = CameraMode.Solar, Navigation = pose } : observer;
    }
}
