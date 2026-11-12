// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal readonly record struct MissionSensorRenderResult(int SurfaceRays, int RangeClips, int Misses, int GridLines,
    double NadirError);

internal sealed class MissionSensorRendering
{
    private readonly ImmediateMesh _faces = new();
    private readonly ImmediateMesh _grid = new();
    private readonly StandardMaterial3D _material = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Back
    };
    private readonly Dictionary<ObjectId, MissionSensorRenderResult> _drawn = new();
    private bool _hasFaces, _hasGrid;

    public MissionSensorRendering(Node3D viewport)
    {
        Add("MissionSensorFaces", _faces);
        Add("MissionSensorGrid", _grid);

        void Add(string name, Mesh mesh) => viewport.AddChild(new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        });
    }

    public MissionSensorRenderResult? Result(ObjectId id) => _drawn.TryGetValue(id, out var result) ? result : null;

    public void Clear()
    {
        if (_hasFaces)
            _faces.ClearSurfaces();
        if (_hasGrid)
            _grid.ClearSurfaces();
        _hasFaces = _hasGrid = false;
        _drawn.Clear();
    }

    public void Finish()
    {
        if (_hasFaces)
            _faces.SurfaceEnd();
        if (_hasGrid)
            _grid.SurfaceEnd();
    }

    public void Draw(ObjectId id, MissionSensor sensor, Vector3d position, Basis3d attitude, WorldSnapshot snapshot,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        var (apex, scale) = project(position);
        if (sensor.RangeKilometers * scale / units < .015 ||
            !snapshot.TryState(sensor.Target, out var target))
            return;
        double radius = NavigationCamera.BodyRadiusKilometers(sensor.Target);
        double polar = PlanetCatalogue.Planets.FirstOrDefault(planet => planet.Id == sensor.Target)?.PolarRadiusKilometers ?? radius;
        if (!(radius > 0) || !(polar > 0))
            return;
        Basis3d toWorld = snapshot.TryOrientation(sensor.Target, out var targetFrame) ? targetFrame.ToWorld : Basis3d.Identity;
        Basis3d toFixed = toWorld.Transpose();
        var axes = new Vector3d(radius, radius, polar);
        Vector3d origin = toFixed.Transform(position - target.PositionKilometers);
        var rim = new Vector3[sensor.Boundary.Count];
        int surfaceRays = 0, rangeClips = 0, misses = 0;
        for (int index = 0; index < rim.Length; index++)
        {
            Vector3d direction = toFixed.Transform(attitude.Transform(sensor.Boundary[index]));
            var ray = MissionSensorProjection.Project(origin, direction, axes, sensor.RangeKilometers);
            rim[index] = project(target.PositionKilometers + toWorld.Transform(ray.FrustumEnd)).Position;
            if (ray.SurfacePoint.HasValue)
                surfaceRays++;
            else if (MissionSensorProjection.TryCrossing(origin, direction, axes, out _))
                rangeClips++;
            else
                misses++;
        }

        var color = new Color((float)sensor.Color.X, (float)sensor.Color.Y, (float)sensor.Color.Z);
        var fill = color with { A = (float)sensor.Opacity };
        var lattice = color with { A = .15f };
        const int rings = 8;
        for (int index = 0; index < rim.Length; index++)
        {
            Vector3 first = rim[index], second = rim[(index + 1) % rim.Length];
            if (sensor.Opacity > 0)
                Face(apex, first, second, fill);
            Line(first, second, color);
            for (int ring = 1; ring < rings; ring++)
            {
                float fraction = ring / (float)rings;
                Line(apex.Lerp(first, fraction), apex.Lerp(second, fraction), lattice);
            }
        }
        for (int index = 0; index < rim.Length; index += rim.Length / sensor.GridSpokes)
            Line(apex, rim[index], lattice);

        Vector3d nadir = target.PositionKilometers - position;
        double nadirError = (attitude.Transform(sensor.Boresight) - nadir / nadir.Length).Length;
        _drawn[id] = new(surfaceRays, rangeClips, misses, (rings - 1) * rim.Length + sensor.GridSpokes, nadirError);
    }

    private void Face(Vector3 a, Vector3 b, Vector3 c, Color color)
    {
        if (!_hasFaces)
        {
            _faces.SurfaceBegin(Mesh.PrimitiveType.Triangles, _material);
            _hasFaces = true;
        }
        Vertex(_faces, a, color);
        Vertex(_faces, b, color);
        Vertex(_faces, c, color);
    }

    private void Line(Vector3 a, Vector3 b, Color color)
    {
        if (!_hasGrid)
        {
            _grid.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
            _hasGrid = true;
        }
        Vertex(_grid, a, color);
        Vertex(_grid, b, color);
    }

    private static void Vertex(ImmediateMesh mesh, Vector3 point, Color color)
    {
        mesh.SurfaceSetColor(color);
        mesh.SurfaceAddVertex(point);
    }
}
