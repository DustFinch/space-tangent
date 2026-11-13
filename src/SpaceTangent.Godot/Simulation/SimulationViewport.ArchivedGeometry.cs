// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly List<MeshInstance3D> _archivedBodies = [];
    private readonly Dictionary<string, MeshInstance3D> _archivedVisible = new(StringComparer.Ordinal);
    private readonly SphereMesh _archivedSphere = new() { Radius = 1, Height = 2, RadialSegments = 48, Rings = 24 };
    private Shader? _archivedBodyShader;
    private Texture2D? _archivedMoonMap;
    private int _archivedUsed;

    internal RecordedSceneGeometry? ArchivedGeometry { get; set; }
    internal MeshInstance3D? ArchivedBody(string name) => _archivedVisible.GetValueOrDefault(name);

    private void DrawArchivedBodies(double units)
    {
        HideArchivedBodies();
        if (ArchivedGeometry is not { } scene)
            return;

        _navigationSun.Hide();
        _navigationEarth.Hide();
        _navigationMoon.Hide();
        HidePlanetGlobes();
        HideImportedBodies();
        HideComets();
        _localSatelliteMarker?.Hide();
        foreach (var (_, satellite) in _satellites.Values)
            satellite.Hide();

        Basis camera = _navigationCamera.Basis;
        double scale = scene.KilometersPerUnit / units;
        Plane[] frustum = _navigationCamera.GetFrustum().ToArray();
        foreach (var body in scene.Bodies)
        {
            if (body.CameraPosition.Z >= body.Radius)
                continue;
            double distance = body.CameraPosition.Length * scale;
            double projection = distance > NavigationFar * .8 ? NavigationFar * .8 / distance : 1;
            float radius = (float)(body.Radius * scale * projection);
            if (radius < .001f)
                continue;
            Vector3 local = Column(body.CameraPosition * (scale * projection));
            Vector3 center = _navigationCamera.Position + camera * local;
            var rotation = body.CameraBasis;
            double maximumAxis = Math.Sqrt(Vector3d.Dot(rotation.X, rotation.X) + Vector3d.Dot(rotation.Y, rotation.Y) + Vector3d.Dot(rotation.Z, rotation.Z));
            if (frustum.Any(plane => plane.DistanceTo(center) > radius * maximumAxis))
                continue;

            if (_archivedBodies.Count == _archivedUsed)
            {
                _archivedBodyShader ??= GD.Load<Shader>("res://Shaders/archived_body.gdshader");
                var mesh = new MeshInstance3D
                {
                    MaterialOverride = new ShaderMaterial { Shader = _archivedBodyShader },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                AddChild(mesh);
                _archivedBodies.Add(mesh);
            }
            var node = _archivedBodies[_archivedUsed++];
            _archivedVisible.TryAdd(body.Name, node);
            var basis = new Basis(Column(rotation.X), Column(rotation.Y) * (float)body.PolarScale, Column(rotation.Z));
            var material = (ShaderMaterial)node.MaterialOverride;
            var id = body.Id;
            var source = id.HasValue ? _planetGlobes.GetValueOrDefault(id.Value) : null;
            var globeMaterial = source?.MaterialOverride as StandardMaterial3D;
            // The archive's spherical surfaces use a quarter-turn about the pole.
            if (source?.Mesh is ArrayMesh model)
                node.Mesh = model;
            else
            {
                node.Mesh = _archivedSphere;
                basis *= new Basis(Vector3.Up, Mathf.Pi / 2);
            }
            node.Transform = new(camera * basis.Scaled(Vector3.One * radius), center);
            Texture2D? texture = id == SolarSystemSample.MoonId ? _archivedMoonMap : globeMaterial?.AlbedoTexture;
            material.SetShaderParameter("has_map", texture is not null);
            if (texture is not null)
                material.SetShaderParameter("surface_map", texture);
            material.SetShaderParameter("tint", id == SolarSystemSample.SunId ? new Color("ffd18d") :
                id == SolarSystemSample.EarthId ? new Color("4a76ad") : globeMaterial?.AlbedoColor ?? new Color("b8b0a6"));
            material.SetShaderParameter("emissive", id == SolarSystemSample.SunId);
            material.SetShaderParameter("camera_light", Column(body.LightDirection));
            node.Show();
        }

        DrawArchivedOverlays(scene, scale);

        static Vector3 Column(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);
    }

    private void HideArchivedBodies()
    {
        for (int index = 0; index < _archivedUsed; index++)
            _archivedBodies[index].Hide();
        _archivedUsed = 0;
        _archivedVisible.Clear();
        _archivedOverlayRoot?.Hide();
        _archivedSymbolCanvas?.Hide();
    }
}
