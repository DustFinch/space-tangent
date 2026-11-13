// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private static readonly Color[] MinorBodyColors =
    [
        new(.78f, .76f, .7f), new(1f, .45f, .3f), new(.45f, .95f, .5f),
        new(1f, .85f, .35f), new(.45f, .7f, 1f), new(.5f, 1f, 1f)
    ];
    private MinorBodyEntry[] _minorBodyEntries = [];
    private ObjectCatalogue? _minorBodyCatalogue;
    private bool[] _minorBodyDuplicates = [];
    private Vector3d[] _minorBodyPositions = [];
    private JulianInstant? _minorBodyEpoch;
    private MeshInstance3D? _minorBodyPoints;
    private ImmediateMesh? _minorBodyMesh;
    internal int MinorBodyPointCount { get; private set; }

    internal void SetMinorBodySwarm(MinorBodyEntry[] entries)
    {
        _minorBodyEntries = entries;
        _minorBodyPositions = new Vector3d[entries.Length];
        _minorBodyEpoch = null;
        _minorBodyCatalogue = null;
    }

    private void HideMinorBodySwarm()
    {
        _minorBodyPoints?.Hide();
        MinorBodyPointCount = 0;
    }

    private void DrawMinorBodySwarm(WorldSnapshot snapshot, Vector3d sun, Vector3d eye, MinorBodyDisplay mode,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideMinorBodySwarm();
        if (mode == MinorBodyDisplay.Off || _minorBodyEntries.Length == 0 || CatalogueWorld is not { } world ||
            DisplayServer.GetName() == "headless")
            return;
        if (!ReferenceEquals(_minorBodyCatalogue, world.Catalogue))
        {
            _minorBodyCatalogue = world.Catalogue;
            _minorBodyEpoch = null;
            var known = world.Catalogue.Objects.Where(item => item.Kind is CatalogueKind.Asteroid or CatalogueKind.Comet or CatalogueKind.DwarfPlanet)
                .SelectMany(item => item.Aliases.Prepend(item.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _minorBodyDuplicates = _minorBodyEntries.Select(entry => known.Contains(entry.Designation) ||
                (entry.Name.Length != 0 && (known.Contains(entry.Name) ||
                    known.Contains($"{entry.Designation} {entry.Name}") ||
                    known.Contains($"{entry.Designation} ({entry.Name})")))).ToArray();
        }
        if (_minorBodyEpoch != snapshot.Epoch)
        {
            for (int index = 0; index < _minorBodyEntries.Length; index++)
                if (!_minorBodyDuplicates[index])
                    _minorBodyPositions[index] = _minorBodyEntries[index].Position(snapshot.Epoch);
            _minorBodyEpoch = snapshot.Epoch;
        }
        if (_minorBodyMesh is null)
        {
            _minorBodyMesh = new();
            _minorBodyPoints = new MeshInstance3D
            {
                Name = "MinorBodySwarm", Mesh = _minorBodyMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/minor_body_points.gdshader") }
            };
            AddChild(_minorBodyPoints);
        }

        _minorBodyMesh.ClearSurfaces();
        var observer = eye - sun;
        float floor = mode == MinorBodyDisplay.Diagram ? .45f : 0;
        var frustum = _navigationCamera.GetFrustum();
        for (int index = 0; index < _minorBodyEntries.Length; index++)
        {
            if (_minorBodyDuplicates[index])
                continue;
            var entry = _minorBodyEntries[index];
            var local = _minorBodyPositions[index];
            var (point, _) = project(sun + local);
            bool outside = false;
            foreach (Plane plane in frustum)
                if (plane.DistanceTo(point) > 0)
                {
                    outside = true;
                    break;
                }
            if (outside)
                continue;
            double magnitude = entry.Magnitude(local, observer);
            float alpha = Math.Clamp(Math.Max((float)((7 - magnitude) * .19), floor), 0, 1);
            if (alpha <= .004f)
                continue;
            if (MinorBodyPointCount == 0)
                _minorBodyMesh.SurfaceBegin(Mesh.PrimitiveType.Points);
            _minorBodyMesh.SurfaceSetColor(new(MinorBodyColors[entry.Group], alpha));
            _minorBodyMesh.SurfaceAddVertex(point);
            MinorBodyPointCount++;
        }
        if (MinorBodyPointCount > 0)
        {
            _minorBodyMesh.SurfaceEnd();
            _minorBodyPoints!.Show();
        }
    }
}
