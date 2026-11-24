// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private StellarRendering? _stars;
    private StellarRendering Stars => _stars ??= new(this, color => CreateNavigationSphere(color, emissive: true));
    private Shader _stellarShader => Stars.Shader;
    internal SimulationWorld? CatalogueWorld { get; private set; }
    internal int VisibleDistantStars => _stars?.VisibleDistantStars ?? 0;
    internal bool StellarDiskResolved => _stars?.StellarDiskResolved == true;
    internal int VisibleStellarDisks => _stars?.VisibleStellarDisks ?? 0;
    internal bool SpatialStarFieldVisible => Stars.SpatialStarFieldVisible;

    internal void SetCatalogueWorld(SimulationWorld world)
    {
        Stars.SetWorld(CatalogueWorld, world);
        if (!ReferenceEquals(CatalogueWorld?.DeepSky, world.DeepSky) || CatalogueWorld?.StellarOrigin != world.StellarOrigin)
            CacheDeepSky(world);
        if (!ReferenceEquals(CatalogueWorld, world))
        {
            foreach (ObjectId id in _bodyLabels.Keys.Where(id => world.Catalogue.Find(id) is null).ToArray())
            {
                _bodyLabels[id].QueueFree();
                _bodyLabels.Remove(id);
            }
            foreach (ObjectId id in _localSatelliteMarkers.Keys.Where(id => world.FindLocalSatellite(id) is null).ToArray())
            {
                _localSatelliteMarkers[id].QueueFree();
                _localSatelliteMarkers.Remove(id);
            }
            foreach (ObjectId id in _bodyPoints.Keys.Where(id => world.Catalogue.Find(id) is null).ToArray())
            {
                _bodyPoints[id].QueueFree();
                _bodyPoints.Remove(id);
            }
        }
        CatalogueWorld = world;
        _spaceSky?.SetCultureWorld(world);
        _surface?.SetCultureWorld(world);
    }

    private void InitializeStellarRendering() => Stars.Initialize(_navigationCamera);

    private void DrawStellarObjects(NavigationCamera pose, WorldSnapshot snapshot, SkyLayerSettings sky, Vector3d placementOffset,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideStellarObjects();
        if (CatalogueWorld is not { } world)
            return;
        if (pose.Target is { } starId && world.FindStellarTarget(world.Exoplanets.Find(starId)?.Host ?? starId) is { } star)
            Stars.DrawSystem(star, world, snapshot, units, project, _frameDeltaT);
        Vector3 localEye = _navigationCamera.Position;
        Vector3d eyeOffset = _navigationEyeOffset +
            new Vector3d(localEye.X, -localEye.Z, localEye.Y) * units;
        Vector3d eye = pose.AnchorKilometers + eyeOffset;
        DrawDeepSky(world, pose, sky, eye, units, project);
        DrawExoplanets(world, pose, snapshot, placementOffset, units, sky.Labels, project);
        DrawDemoWorlds(pose, snapshot, units, sky, project);
        Stars.DrawFieldIfVisible(eye, snapshot.Epoch, world, pose.Target, sky.Stars);
    }

    private ObjectId? PickDistantStar(Vector2 point) => Stars.Pick(point);

    private void HideStellarObjects()
    {
        Stars.Hide();
        HideDeepSky();
        HideExoplanets();
        HideDemoWorlds();
    }
}
