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
    private const double OuterOverviewKilometersPerUnit = 25_000_000;
    private readonly Dictionary<ObjectId, PlanetRingRendering> _planetRings = new();
    private readonly Dictionary<ObjectId, Node3D> _planetMarkers = new();
    private readonly Dictionary<ObjectId, MeshInstance3D> _planetGlobes = new();
    private readonly Dictionary<ObjectId, WorldOrbitAtmosphere> _worldAtmospheres = new();
    private BodyTileTextures? _marsTiles;
    private readonly ImmediateMesh _planetPaths = new();
    private MeshInstance3D _planetPathNode = null!;

    private JulianInstant? _pathEpoch;
    private bool _pathOuter;

    internal bool ShowPlanetOrbits { get; set; } = true;
    internal int PlanetOrbitCount => _planetPaths.GetSurfaceCount();
    internal bool PlanetRingsVisible(ObjectId id) => _planetRings.GetValueOrDefault(id)?.Visible == true;
    internal bool PlanetGlobeTextured(ObjectId id) => _planetGlobes.TryGetValue(id, out var mesh) && mesh.Visible &&
        (mesh.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null } ||
            mesh.Mesh is ArrayMesh model && model.GetSurfaceCount() > 0 &&
                model.SurfaceGetMaterial(0) is ShaderMaterial material && material.GetShaderParameter("has_diffuse").AsBool() ||
            _planetRings.GetValueOrDefault(id)?.Textured == true ||
            _worldAtmospheres.GetValueOrDefault(id)?.Textured == true);

    private void InitializePlanets()
    {
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (planet.Id == SolarSystemSample.EarthId)
                continue;
            Color color = new(planet.Color);
            _planetMarkers.Add(planet.Id, CreateMarker(planet.Name, color, 2.8f));
            _planetGlobes.Add(planet.Id, CreateNavigationSphere(color));
        }
        _planetPathNode = new MeshInstance3D { Mesh = _planetPaths, Visible = false };
        AddChild(_planetPathNode);

        InitializeSmallBodies();
    }

    private Texture2D? _venusClouds;

    internal void SetPlanetTextures(PlanetTextures textures)
    {
        _venusClouds = textures.VenusClouds;
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

    private void DrawWorldAtmospheres(WorldSnapshot snapshot, Vector3d sun)
    {
        foreach (var (id, atmosphere) in _worldAtmospheres)
        {
            if (!_planetGlobes[id].Visible || !snapshot.TryState(id, out CartesianState state))
                continue;
            Vector3d direction = sun - state.PositionKilometers;
            atmosphere.Update(ToGodot(direction / direction.Length, 1), _navigationCamera.Position, snapshot.Epoch);
        }
    }

    private void DrawPlanetOverview(WorldSnapshot snapshot, ISolarSystemEphemeris ephemeris, Vector3d sun, bool outer)
    {
        double scale = outer ? OuterOverviewKilometersPerUnit : OverviewKilometersPerUnit;
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (!_planetMarkers.TryGetValue(planet.Id, out Node3D? marker))
                continue;
            bool inRange = outer ? planet.NaifId >= 599 : planet.NaifId <= 499;
            if (inRange && snapshot.TryState(planet.Id, out CartesianState state))
            {
                marker.Position = ToGodot(state.PositionKilometers - sun, scale);
                marker.Show();
            }
        }
        if (!ShowPlanetOrbits)
            return;
        if (_pathEpoch is not { } previous || _pathOuter != outer || Math.Abs((double)snapshot.Epoch.DifferenceNanoseconds(previous) / JulianInstant.NanosecondsPerSecond) > 86400 * 40)
        {
            _pathEpoch = snapshot.Epoch;
            _pathOuter = outer;
            _planetPaths.ClearSurfaces();
            foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
            {
                if (outer != (planet.NaifId >= 599))
                    continue;
                double halfPeriod = planet.PeriodDays * 43200;
                JulianInstant start = snapshot.Epoch.AddSeconds(-halfPeriod);
                JulianInstant end = snapshot.Epoch.AddSeconds(halfPeriod);
                if (end.CompareTo(ephemeris.End) > 0)
                    start = ephemeris.End.AddSeconds(-2 * halfPeriod);
                if (start.CompareTo(ephemeris.Start) < 0)
                    end = ephemeris.Start.AddSeconds(2 * halfPeriod);
                if (start.CompareTo(ephemeris.Start) < 0)
                    start = ephemeris.Start;
                if (end.CompareTo(ephemeris.End) > 0)
                    end = ephemeris.End;
                double duration = (double)end.DifferenceNanoseconds(start) / JulianInstant.NanosecondsPerSecond;
                Color color = new(planet.Color);
                color.A = .5f;
                _planetPaths.SurfaceBegin(Mesh.PrimitiveType.LineStrip, LineMaterial(new(color.R, color.G, color.B), color.A));
                const int samples = 120;
                for (int step = 0; step <= samples; step++)
                {
                    SolarSystemSample sample = ephemeris.Sample(step == samples ? end : start.AddSeconds(duration * step / samples));
                    _planetPaths.SurfaceAddVertex(ToGodot(sample.RelativeState(planet.SystemId, SolarSystemSample.SunId).PositionKilometers, scale));
                }
                _planetPaths.SurfaceEnd();
            }
        }
        _planetPathNode.Show();
    }

    private void HidePlanetOverview()
    {
        foreach (Node3D marker in _planetMarkers.Values)
            marker.Hide();
        _planetPathNode.Hide();
    }

    private void DrawPlanetGlobes(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (!_planetGlobes.TryGetValue(planet.Id, out MeshInstance3D? globe) ||
                !snapshot.TryState(planet.Id, out CartesianState state))
                continue;
            var (position, scale) = project(state.PositionKilometers);
            float radius = (float)(planet.RadiusKilometers * scale / units);
            if (radius < .025f && pose.Target != planet.Id)
                continue;
            float pole = (float)(planet.PolarRadiusKilometers / planet.RadiusKilometers);
            bool oriented = PlaceWorldGlobe(globe, snapshot, planet.Id, position, new(radius, radius * pole, radius));
            if (_planetRings.TryGetValue(planet.Id, out PlanetRingRendering? rings))
            {
                Vector3d toSun = snapshot.SolarSystem!.Value.Sun.PositionKilometers - state.PositionKilometers;
                double distance = toSun.Length;
                rings.Update(ToGodot(toSun / distance, 1), distance, oriented);
            }
        }
        DrawSmallBodies(snapshot, pose, units, project);
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
        foreach (MeshInstance3D globe in _planetGlobes.Values)
            globe.Hide();
        foreach (PlanetRingRendering rings in _planetRings.Values)
            rings.Hide();
        foreach (Label3D label in _moonLabels.Values)
            label.Hide();
    }
}
