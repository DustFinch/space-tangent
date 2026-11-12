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
    private const float SaturnRingInner = 74500f / 60268f, SaturnRingOuter = 140220f / 60268f;
    private readonly Dictionary<ObjectId, Node3D> _planetMarkers = new();
    private readonly Dictionary<ObjectId, MeshInstance3D> _planetGlobes = new();
    private readonly Dictionary<ObjectId, WorldOrbitAtmosphere> _worldAtmospheres = new();
    private readonly ImmediateMesh _planetPaths = new();
    private MeshInstance3D _planetPathNode = null!;
    private Node3D _saturnRings = null!;
    private ShaderMaterial? _saturnRingMaterial, _saturnGlobeMaterial;
    private bool _saturnSurfaceTextured;
    private JulianInstant? _pathEpoch;
    private bool _pathOuter;

    internal bool ShowPlanetOrbits { get; set; } = true;
    internal int PlanetOrbitCount => _planetPaths.GetSurfaceCount();
    internal bool SaturnRingsVisible => _saturnRings.Visible;
    internal bool PlanetGlobeTextured(ObjectId id) => _planetGlobes.TryGetValue(id, out var mesh) && mesh.Visible &&
        (mesh.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null } ||
            (id == PlanetCatalogue.SaturnId && _saturnSurfaceTextured) ||
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
        _saturnRings = new Node3D { Visible = false };
        AddChild(_saturnRings);
        InitializeSmallBodies();
    }

    internal void SetPlanetTextures(PlanetTextures textures)
    {
        _marsSurface = textures.Surfaces.GetValueOrDefault(PlanetCatalogue.MarsId);
        foreach (var (id, texture) in textures.Surfaces)
        {
            var material = (StandardMaterial3D)_planetGlobes[id].MaterialOverride;
            material.AlbedoTexture = texture;
            material.AlbedoColor = Colors.White;
        }
        if (textures.Rings is not { } rings)
            return;
        var mesh = new ImmediateMesh();
        _saturnRingMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/saturn_rings.gdshader") };
        _saturnRingMaterial.SetShaderParameter("rings_map", rings);
        _saturnGlobeMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/saturn_globe.gdshader") };
        _saturnGlobeMaterial.SetShaderParameter("rings_map", rings);
        _saturnGlobeMaterial.SetShaderParameter("ring_inner", SaturnRingInner);
        _saturnGlobeMaterial.SetShaderParameter("ring_outer", SaturnRingOuter);
        _saturnSurfaceTextured = textures.Surfaces.TryGetValue(PlanetCatalogue.SaturnId, out Texture2D? surface);
        _saturnGlobeMaterial.SetShaderParameter("has_surface_map", _saturnSurfaceTextured);
        if (surface is not null)
            _saturnGlobeMaterial.SetShaderParameter("surface_map", surface);
        _planetGlobes[PlanetCatalogue.SaturnId].MaterialOverride = _saturnGlobeMaterial;
        const int segments = 192;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, _saturnRingMaterial);
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.Tau * i / segments;
            Vector3 radial = new(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            mesh.SurfaceSetUV(new(0, .5f));
            mesh.SurfaceAddVertex(radial * SaturnRingInner);
            mesh.SurfaceSetUV(new(1, .5f));
            mesh.SurfaceAddVertex(radial * SaturnRingOuter);
        }
        mesh.SurfaceEnd();
        _saturnRings.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    internal void SetWorldAtmospheres(IReadOnlyDictionary<ObjectId, OrbitAtmosphereTextures> lookups)
    {
        ObjectId venus = SolarSystemSample.VenusId;
        _worldAtmospheres.Add(venus, new(_planetGlobes[venus], 6051.8f, null));
        foreach (var (id, lookup) in lookups)
            if (_planetGlobes.TryGetValue(id, out MeshInstance3D? globe))
                _worldAtmospheres.Add(id, new(globe, lookup.RadiusKilometers, lookup));
    }

    internal bool WorldAtmosphereActive(ObjectId id, bool lookup) => id == SolarSystemSample.EarthId
        ? _earthOrbit is { Active: true } air && air.UsesLookup == lookup
        : _worldAtmospheres.TryGetValue(id, out var atmosphere) && atmosphere.Active && atmosphere.UsesLookup == lookup;

    private void DrawWorldAtmospheres(WorldSnapshot snapshot, Vector3d sun)
    {
        foreach (var (id, atmosphere) in _worldAtmospheres)
        {
            if (!_planetGlobes[id].Visible || !snapshot.TryState(id, out CartesianState state))
                continue;
            Vector3d direction = sun - state.PositionKilometers;
            atmosphere.Update(ToGodot(direction / direction.Length, 1), _navigationCamera.Position);
        }
    }

    private void DrawPlanetOverview(WorldSnapshot snapshot, De440Ephemeris ephemeris, Vector3d sun, bool outer)
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
            if (planet.NaifId != 699 || _saturnRingMaterial is null || _saturnGlobeMaterial is null)
                continue;
            Basis basis = globe.Transform.Basis.Orthonormalized();
            Vector3d toSun = snapshot.SolarSystem!.Value.Sun.PositionKilometers - state.PositionKilometers;
            Vector3 light = basis.Inverse() * ToGodot(toSun / toSun.Length, 1);
            bool ringsVisible = oriented && radius > .12f;
            _saturnGlobeMaterial.SetShaderParameter("sun_in_body", light);
            _saturnGlobeMaterial.SetShaderParameter("polar_ratio", pole);
            _saturnGlobeMaterial.SetShaderParameter("cast_ring_shadows", ringsVisible);
            if (!ringsVisible)
                continue;
            _saturnRingMaterial.SetShaderParameter("sun_in_body", light);
            _saturnRingMaterial.SetShaderParameter("polar_ratio", pole);
            _saturnRingMaterial.SetShaderParameter("solar_radius_radians", (float)Math.Asin(SolarOccultation.SunRadiusKilometers / toSun.Length));
            _saturnRings.Transform = new(basis * Basis.FromScale(Vector3.One * radius), position);
            _saturnRings.Show();
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
        _saturnRings.Hide();
        _cometTail.Hide();
        foreach (Label3D label in _moonLabels.Values)
            label.Hide();
    }
}
