// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed class PlanetOverviewRendering
{
    internal const double InnerScale = 1_000_000, OuterScale = 25_000_000;
    private readonly Node3D _parent;
    private readonly Func<string, Color, float, Node3D> _createMarker;
    private readonly Func<Vector3d, double, StandardMaterial3D> _lineMaterial;
    private readonly Dictionary<ObjectId, Node3D> _planetMarkers = new();
    private ImmediateMesh _planetPaths = new();
    private readonly List<StandardMaterial3D> _materials = [];
    private MeshInstance3D _planetPathNode = null!;
    private JulianInstant? _pathEpoch;
    private bool _pathOuter;
    internal int OrbitCount => _closed ? 0 : _planetPaths.GetSurfaceCount();

    internal PlanetOverviewRendering(Node3D parent, Func<string, Color, float, Node3D> createMarker,
        Func<Vector3d, double, StandardMaterial3D> lineMaterial)
    { _parent = parent; _createMarker = createMarker; _lineMaterial = lineMaterial; }
    internal void AddPlanet(PlanetDefinition planet) =>
        _planetMarkers.Add(planet.Id, _createMarker(planet.Name, new Color(planet.Color), 2.8f));
    internal void InitializePaths()
    {
        _planetPathNode = new MeshInstance3D { Mesh = _planetPaths, Visible = false };
        _parent.AddChild(_planetPathNode);
    }
    private bool _closed;
    internal void Close()
    {
        if (_closed) return;
        _closed = true;
        foreach (var marker in _planetMarkers.Values) marker.Hide();
        _planetMarkers.Clear();
        if (_planetPathNode is not null) { _planetPathNode.Hide(); _planetPathNode.Mesh = null; }
        ReleaseMaterials();
        _planetPaths.Dispose();
        _planetPaths = null!;
    }
    private void ReleaseMaterials()
    {
        foreach (var material in _materials) material.Dispose();
        _materials.Clear();
    }

    internal void Draw(WorldSnapshot snapshot, ISolarSystemEphemeris ephemeris, Vector3d sun, bool outer, bool showOrbits)
    {
        if (_closed) return;
        double scale = outer ? OuterScale : InnerScale;
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
        if (!showOrbits)
            return;
        if (_pathEpoch is not { } previous || _pathOuter != outer || Math.Abs((double)snapshot.Epoch.DifferenceNanoseconds(previous) / JulianInstant.NanosecondsPerSecond) > 86400 * 40)
        {
            _pathEpoch = snapshot.Epoch;
            _pathOuter = outer;
            _planetPaths.ClearSurfaces();
            ReleaseMaterials();
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
                var material = _lineMaterial(new(color.R, color.G, color.B), color.A);
                _materials.Add(material);
                _planetPaths.SurfaceBegin(Mesh.PrimitiveType.LineStrip, material);
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

    internal void Hide()
    {
        foreach (Node3D marker in _planetMarkers.Values)
            marker.Hide();
        _planetPathNode.Hide();
    }

    private static Vector3 ToGodot(Vector3d value, double scale) =>
        new((float)(value.X / scale), (float)(value.Z / scale), (float)(-value.Y / scale));
}
