// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const double OuterOverviewKilometersPerUnit = 25_000_000;
    private readonly Dictionary<ObjectId, Node3D> _planetMarkers = new();
    private readonly Dictionary<ObjectId, MeshInstance3D> _planetGlobes = new();
    private readonly ImmediateMesh _planetPaths = new();
    private MeshInstance3D _planetPathNode = null!;
    private Node3D _saturnRings = null!;
    private JulianInstant? _pathEpoch;
    private bool _pathOuter;

    internal bool ShowPlanetOrbits { get; set; } = true;
    internal int PlanetOrbitCount => _planetPaths.GetSurfaceCount();
    internal bool SaturnRingsVisible => _saturnRings.Visible;
    internal bool PlanetGlobeTextured(ObjectId id) => _planetGlobes.TryGetValue(id, out var mesh) && mesh.Visible &&
        mesh.MaterialOverride is StandardMaterial3D { AlbedoTexture: not null };

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
        var materialRings = new StandardMaterial3D
        {
            AlbedoTexture = rings,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            TextureRepeat = false
        };
        const int segments = 192;
        mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, materialRings);
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.Tau * i / segments;
            Vector3 radial = new(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            mesh.SurfaceSetUV(new(0, .5f));
            mesh.SurfaceAddVertex(radial * (74500f / 60268f));
            mesh.SurfaceSetUV(new(1, .5f));
            mesh.SurfaceAddVertex(radial * (140220f / 60268f));
        }
        mesh.SurfaceEnd();
        _saturnRings.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
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
            bool oriented = snapshot.TryOrientation(planet.Id, out FrameKinematics rotation);
            Basis basis = oriented ? PlanetBasis(rotation.ToWorld) : Basis.Identity;
            float pole = (float)(planet.PolarRadiusKilometers / planet.RadiusKilometers);
            globe.Transform = new(basis * Basis.FromScale(new(radius, radius * pole, radius)), position);
            globe.Show();
            if (planet.NaifId == 699 && oriented && radius > .12f && _saturnRings.GetChildCount() != 0)
            {
                _saturnRings.Transform = new(basis * Basis.FromScale(Vector3.One * radius), position);
                _saturnRings.Show();
            }
        }
    }

    private static Basis PlanetBasis(Basis3d fixedToIcrf) =>
        new(ToGodot(fixedToIcrf.X, 1), ToGodot(fixedToIcrf.Z, 1), ToGodot(fixedToIcrf.Y * -1, 1));

    private void HidePlanetGlobes()
    {
        foreach (MeshInstance3D globe in _planetGlobes.Values)
            globe.Hide();
        _saturnRings.Hide();
    }
}
