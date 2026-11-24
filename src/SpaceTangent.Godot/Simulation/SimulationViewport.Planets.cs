// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const double OuterOverviewKilometersPerUnit = PlanetOverviewRendering.OuterScale;
    private PlanetOverviewRendering _planetOverview = null!;
    private readonly Dictionary<ObjectId, PlanetRingRendering> _planetRings = new();
    private readonly Dictionary<ObjectId, MeshInstance3D> _planetGlobes = new();
    private readonly Dictionary<ObjectId, WorldOrbitAtmosphere> _worldAtmospheres = new();
    private BodyTileTextures? _marsTiles;


    internal bool ShowPlanetOrbits { get; set; } = true;
    internal int PlanetOrbitCount => _planetOverview?.OrbitCount ?? 0;
    internal bool PlanetRingsVisible(ObjectId id) => _planetRings.GetValueOrDefault(id)?.Visible == true;
    internal MeshInstance3D? RingedAtmosphere(ObjectId id) => _planetRings.GetValueOrDefault(id)?.Atmosphere;
    internal bool PlanetGlobeTextured(ObjectId id) => _planetGlobes.TryGetValue(id, out var mesh) && mesh.Visible &&
        (mesh.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null } ||
            mesh.Mesh is ArrayMesh model && model.GetSurfaceCount() > 0 &&
                model.SurfaceGetMaterial(0) is ShaderMaterial material && material.GetShaderParameter("has_diffuse").AsBool() ||
            _planetRings.GetValueOrDefault(id)?.Textured == true ||
            _worldAtmospheres.GetValueOrDefault(id)?.Textured == true);

    private void InitializePlanets()
    {
        _planetOverview = new(this, CreateMarker, LineMaterial);
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (planet.Id == SolarSystemSample.EarthId)
                continue;
            Color color = new(planet.Color);
            _planetOverview.AddPlanet(planet);
            _planetGlobes.Add(planet.Id, CreateNavigationSphere(color));
        }
        _planetOverview.InitializePaths();

        InitializeSmallBodies();
    }

    private Texture2D? _venusClouds;

    internal void SetPlanetTextures(PlanetTextures textures, Texture2DArray? groundMaps = null)
    {
        _surface?.SetPlanetMaps(textures.Surfaces, groundMaps);
        _venusClouds = textures.VenusClouds;
        _archivedPlanetTextures = textures;
        _marsSurface = textures.Surfaces.GetValueOrDefault(PlanetCatalogue.MarsId);
        foreach (var (id, texture) in textures.Surfaces)
        {
            var material = (StandardMaterial3D)_planetGlobes[id].MaterialOverride;
            material.AlbedoTexture = texture;
            material.Uv1Offset = new(.25f, 0, 0); // The sphere seam is 90° west of the map seam.
            material.AlbedoColor = Colors.White;
        }
        foreach (PlanetRingProfile profile in PlanetRingRendering.Profiles)
            if (textures.Rings.TryGetValue(profile.Id, out var texture) && PlanetCatalogue.Find(profile.Id) is { } body)
                _planetRings.Add(profile.Id, new(this, _planetGlobes[profile.Id], profile, texture, body.RadiusKilometers));
    }

    internal void SetWorldAtmospheres(IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> lookups,
        IReadOnlyDictionary<ObjectId, BodyTileTextures> tiles)
    {
        _archivedAtmosphereLookups = lookups;
        _marsTiles = tiles.GetValueOrDefault(PlanetCatalogue.MarsId);
        ObjectId venus = SolarSystemSample.VenusId;
        _worldAtmospheres.Add(venus, new(_planetGlobes[venus], 6051.8f, null, tiles.GetValueOrDefault(venus), hasAtmosphere: true, venusClouds: _venusClouds, venusScattering: true));
        foreach (var (id, lookup) in lookups)
            if (_planetGlobes.TryGetValue(id, out MeshInstance3D? globe))
                _worldAtmospheres.Add(id, new(globe, lookup.RadiusKilometers, lookup, tiles.GetValueOrDefault(id), hasAtmosphere: true));
        foreach (var (id, maps) in tiles)
        {
            if (!_worldAtmospheres.ContainsKey(id) && _planetGlobes.TryGetValue(id, out var globe) &&
                globe.MaterialOverride is StandardMaterial3D)
                _worldAtmospheres.Add(id, new(globe, (float)NavigationCamera.BodyRadiusKilometers(id), null, maps, hasAtmosphere: false));
        }
    }

    internal BodyTileTextures? LocalSurfaceTiles(ObjectId id) => _worldAtmospheres.GetValueOrDefault(id)?.Tiles;

    internal bool WorldAtmosphereActive(ObjectId id, bool lookup) => id == SolarSystemSample.EarthId
        ? _earthOrbit is { Active: true } air && air.UsesLookup == lookup
        : _worldAtmospheres.TryGetValue(id, out var atmosphere) && atmosphere.Active && atmosphere.UsesLookup == lookup;

    internal (bool Visible, float Offset)? VenusCloudState => _worldAtmospheres.GetValueOrDefault(SolarSystemSample.VenusId)?.CloudState;

    private void DrawWorldAtmospheres(WorldSnapshot snapshot, Vector3d sun, SkyLayerSettings sky)
    {
        foreach (var (id, atmosphere) in _worldAtmospheres)
        {
            if (!_planetGlobes[id].Visible || !snapshot.TryState(id, out CartesianState state))
                continue;
            Vector3d direction = sun - state.PositionKilometers;
            Vector3d occluder = default;
            double radius = 0;
            if (NaturalBodyCatalogue.Find(id) is { Kind: CatalogueKind.Moon } moon &&
                snapshot.TryState(moon.Parent, out var parent))
            {
                occluder = (parent - state).PositionKilometers;
                double parentRadius = NavigationCamera.BodyRadiusKilometers(moon.Parent);
                if (SolarOccultation.VisibleFraction(direction, occluder, parentRadius + moon.RadiusKilometers + 1000) < 1)
                    radius = parentRadius;
            }
            atmosphere.Update(direction, _navigationCamera.Position, snapshot.Epoch, !sky.HideAtmospheres, !sky.HideClouds, occluder, radius);
        }
    }

    private void DrawPlanetOverview(WorldSnapshot snapshot, ISolarSystemEphemeris ephemeris, Vector3d sun, bool outer) =>
        _planetOverview.Draw(snapshot, ephemeris, sun, outer, ShowPlanetOrbits);
    private void HidePlanetOverview() => _planetOverview.Hide();

    private void DrawPlanetGlobes(WorldSnapshot snapshot, NavigationCamera pose, Vector3d eyeOffset, double units, SkyLayerSettings sky, ObjectId? selection,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (!_planetGlobes.TryGetValue(planet.Id, out MeshInstance3D? globe) ||
                !snapshot.TryState(planet.Id, out CartesianState state))
                continue;
            var (position, scale) = project(state.PositionKilometers);
            float radius = (float)(planet.RadiusKilometers * scale / units);
            Vector3d towardEye = (pose.AnchorKilometers - state.PositionKilometers) + eyeOffset;
            if (sky.Labels)
                DrawBodyLabel(planet.Id, planet.Name, SolarSystemSample.SunId, PlanetLabelOrbitRadius(planet.NaifId),
                    planet.RadiusKilometers, snapshot, state.PositionKilometers, towardEye, position, radius);
            if (!DrawReflectedBody(planet.Id, SolarSystemSample.SunId, planet.RadiusKilometers, PlanetAlbedo(planet.NaifId),
                new Color(planet.Color), snapshot, towardEye, state.PositionKilometers, position, radius))
                continue;
            float pole = (float)(planet.PolarRadiusKilometers / planet.RadiusKilometers);
            bool oriented = PlaceWorldGlobe(globe, snapshot, planet.Id, position, new(radius, radius * pole, radius));
            if (_planetRings.TryGetValue(planet.Id, out PlanetRingRendering? rings))
            {
                Vector3d toSun = snapshot.SolarSystem!.Value.Sun.PositionKilometers - state.PositionKilometers;
                double distance = toSun.Length;
                rings.Update(ToGodot(toSun / distance, 1), distance, oriented, !sky.HideAtmospheres);
            }
        }
        DrawSmallBodies(snapshot, pose, eyeOffset, units, sky, selection, project);
    }

    private static bool PlaceWorldGlobe(MeshInstance3D globe, WorldSnapshot snapshot, ObjectId id, Vector3 position, Vector3 radii)
    {
        bool oriented = snapshot.TryOrientation(id, out FrameKinematics rotation);
        Basis basis = oriented ? PlanetBasis(rotation.ToWorld) : Basis.Identity;
        globe.Transform = new(basis * Basis.FromScale(radii), position);
        globe.Show();
        return oriented;
    }

    private static Basis PlanetBasis(Basis3d fixedToIcrf) =>
        new(ToGodot(fixedToIcrf.X, 1), ToGodot(fixedToIcrf.Z, 1), ToGodot(fixedToIcrf.Y * -1, 1));

    private void HidePlanetGlobes()
    {
        foreach (var point in _bodyPoints.Values)
            point.Hide();
        foreach (MeshInstance3D globe in _planetGlobes.Values)
            globe.Hide();
        foreach (PlanetRingRendering rings in _planetRings.Values)
            rings.Hide();
        foreach (Label3D label in _bodyLabels.Values)
            label.Hide();
    }
}
