// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const int MaximumDeepSkyMeshes = 64;
    private readonly List<(MeshInstance3D Mesh, Label3D Label, ShaderMaterial Profile)> _deepSkyPool = [];
    private readonly Dictionary<ObjectId, MeshInstance3D> _deepSkyObjects = new();
    private readonly List<(DeepSkyObject Body, Vector3d Center, double Score)> _deepSkyCandidates = [];
    private (DeepSkyObject Body, Vector3d Center, double Radius)[] _deepSkyPositions = [];
    private Shader? _deepSkyShader;
    private QuadMesh? _deepSkyQuad;

    internal bool DeepSkyVisible(ObjectId id) => IsVolumetricGalaxy(id) || DeepSkySpriteVisible(id);
    internal bool DeepSkySpriteVisible(ObjectId id) => _deepSkyObjects.GetValueOrDefault(id)?.Visible == true;

    internal bool DeepSkyLabelVisible(ObjectId id) => _deepSkyObjects.TryGetValue(id, out var mesh) &&
        _deepSkyPool.Any(item => item.Mesh == mesh && item.Label.Visible);

    private void CacheDeepSky(SimulationWorld world) => _deepSkyPositions = world.DeepSky.Objects
        .Select(body => (body, body.PositionKilometers + world.StellarOrigin, body.RadiusKilometers)).ToArray();

    private void DrawDeepSky(SimulationWorld world, NavigationCamera pose, SkyLayerSettings sky, Vector3d eye,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        var filter = sky.DeepSkyFilter ?? DeepSkyVisibility.All;
        var target = pose.Target is { } id ? world.DeepSky.Find(id) : null;
        if (target is not null && !filter.Allows(target))
            target = null;
        if (!sky.DeepSky && target is null)
            return;
        _deepSkyCandidates.Clear();
        if (target is not null)
            _deepSkyCandidates.Add((target, target.PositionKilometers + world.StellarOrigin, double.PositiveInfinity));
        if (sky.DeepSky)
        {
            Vector3 forward = -_navigationCamera.Basis.Z;
            var direction = new Vector3d(forward.X, -forward.Z, forward.Y);
            Vector2 size = GetViewport().GetVisibleRect().Size;
            double aspect = size.Y > 0 ? size.X / size.Y : 1;
            double halfAngle = Math.Atan(Math.Tan(_navigationCamera.Fov * Math.PI / 360) * Math.Sqrt(1 + aspect * aspect));
            double limit = Math.Cos(halfAngle + .20);
            foreach (var (body, center, radius) in _deepSkyPositions)
            {
                if (body.Id == pose.Target)
                    continue;
                Vector3d offset = center - eye;
                double distance = offset.Length;
                if (distance <= radius || radius < distance * .0012 || Vector3d.Dot(offset, direction) < distance * limit || !filter.Allows(body))
                    continue;
                _deepSkyCandidates.Add((body, center, radius / distance));
            }
            _deepSkyCandidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        }

        int shown = 0;
        foreach (var (body, center, _) in _deepSkyCandidates)
        {
            var (position, scale) = project(center);
            float radius = (float)(body.RadiusKilometers * scale / units);
            if (radius < .12f)
                continue;
            if (_deepSkyPool.Count == shown)
                _deepSkyPool.Add(CreateDeepSkyMesh());
            var (mesh, label, profile) = _deepSkyPool[shown++];
            Basis facing = _navigationCamera.Basis;
            bool elliptical = body.Morphology is { Length: 2 } form && form[0] == 'E' && char.IsAsciiDigit(form[1]);
            float aspect = elliptical ? 1 - (body.Morphology![1] - '0') * .095f : body.Kind == DeepSkyKind.Galaxy ? .43f : 1;
            if (DeepSkySpriteMesh(body.Id, body.Orientation) is { } sprites)
            {
                mesh.Mesh = sprites;
                mesh.MaterialOverride = sprites.SurfaceGetMaterial(0);
                mesh.Transform = new(Basis.FromScale(Vector3.One * radius), position);
            }
            else
            {
                mesh.Transform = new(facing * Basis.FromScale(new(radius, radius * aspect, 1)), position);
                mesh.Mesh = _deepSkyQuad;
                mesh.MaterialOverride = profile;
                profile.SetShaderParameter("profile", (int)body.Kind);
                profile.SetShaderParameter("elliptical", elliptical);
                profile.SetShaderParameter("concentration", (float)body.KingConcentration);
            }
            if (IsVolumetricGalaxy(body.Id))
                mesh.Hide();
            else
                mesh.Show();
            bool labelEnabled = body.Kind switch
            {
                DeepSkyKind.OpenCluster => sky.OpenClusterLabels ?? true,
                DeepSkyKind.Nebula => sky.NebulaLabels ?? true,
                _ => true
            };
            if (sky.Labels && labelEnabled)
            {
                label.Text = body.Designation;
                label.Position = position - facing.Y * radius * (aspect + .18f);
                label.Show();
            }
            _deepSkyObjects.Add(body.Id, mesh);
            if (shown == MaximumDeepSkyMeshes)
                break;
        }
    }

    private (MeshInstance3D Mesh, Label3D Label, ShaderMaterial Profile) CreateDeepSkyMesh()
    {
        _deepSkyShader ??= GD.Load<Shader>("res://Shaders/deep_sky.gdshader");
        _deepSkyQuad ??= new QuadMesh { Size = new(2, 2) };
        var profile = new ShaderMaterial { Shader = _deepSkyShader };
        var mesh = new MeshInstance3D
        {
            Mesh = _deepSkyQuad, MaterialOverride = profile, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        Label3D label = CreateLabel("", Vector3.Zero);
        label.FixedSize = true;
        label.PixelSize = .0018f;
        label.NoDepthTest = false;
        label.Hide();
        AddChild(mesh);
        AddChild(label);
        return (mesh, label, profile);
    }

    private void HideDeepSky()
    {
        _deepSkyObjects.Clear();
        foreach (var (mesh, label, _) in _deepSkyPool)
        {
            mesh.Hide();
            label.Hide();
        }
    }
}
