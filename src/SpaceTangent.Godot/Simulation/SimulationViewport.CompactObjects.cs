// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    internal CompactLensTables? CompactTables { get; init; }
    internal bool CinematicCompactLens { get; init; }
    internal bool CompactLensAllowed { get; set; } = true;
    internal bool CompactDiskEmission { get; set; } = true;
    internal bool CompactLensVisible => _compactLens?.Visible == true;
    internal int CompactSkyCaptures => _compactLens?.Captures ?? 0;
    internal bool CompactCompanionVisible => _compactCompanion?.Visible == true;
    private CompactLensRenderer? _compactLens;
    private BrightSkyData? _compactSky;
    private MeshInstance3D? _compactCompanion, _companionGlare;
    private (ObjectId Id, Vector3 Center, float Radius)? _compactFocus;

    private void DrawCompactObjects(WorldSnapshot snapshot, SkyLayerSettings sky,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _compactCompanion?.Hide();
        _companionGlare?.Hide();
        _compactFocus = null;
        if (CatalogueWorld is not { } world || _compactSky is null || CompactTables is null || DisplayServer.GetName() == "headless")
            return;

        Vector3? companion = null;
        float companionRadius = 0;
        if (snapshot.TryState(SimulationWorld.CygnusCompanionId, out var star))
        {
            var (center, projection) = project(star.PositionKilometers);
            float radius = (float)(SimulationWorld.CygnusCompanionRadiusKilometers * projection / units);
            float distance = center.DistanceTo(_navigationCamera.Position);
            if (radius > distance * 2e-5f && !_navigationCamera.IsPositionBehind(center))
            {
                _compactCompanion ??= MakeCompanion();
                _compactCompanion.Position = center;
                _compactCompanion.Scale = Vector3.One * radius;
                _compactCompanion.Show();
                _companionGlare!.Position = center;
                var material = (ShaderMaterial)_companionGlare.MaterialOverride;
                material.SetShaderParameter("star_radius", radius);
                material.SetShaderParameter("clip_near", _navigationCamera.Near);
                _companionGlare.Show();
                companion = center;
                companionRadius = radius;
            }
        }

        BlackHole? strongest = null;
        Vector3 location = default;
        float physicalRadius = 0, ratio = 1e-8f;
        foreach (BlackHole hole in world.BlackHoles.Entries)
        {
            if (!snapshot.TryState(hole.Id, out var state))
                continue;
            var (center, scale) = project(state.PositionKilometers);
            float radius = (float)(hole.SchwarzschildRadiusKilometers * scale / units);
            float distance = center.DistanceTo(_navigationCamera.Position);
            if (distance <= 0 || radius / distance <= ratio)
                continue;
            strongest = hole;
            ratio = radius / distance;
            location = center;
            physicalRadius = radius;
        }
        if (!CompactLensAllowed || strongest is null)
        {
            _compactLens?.Hide();
            return;
        }
        _compactLens ??= new(this, _compactSky, CompactTables);
        _compactLens.Draw(_navigationCamera, strongest, location, physicalRadius, snapshot.Epoch, sky,
            _galacticTexture, SpatialStarFieldVisible, CinematicCompactLens || sky.CinematicBlackHoles, companion, companionRadius, CompactDiskEmission);
        _compactFocus = (strongest.Id, location, physicalRadius);
    }

    private MeshInstance3D MakeCompanion()
    {
        var companion = CreateNavigationSphere(Colors.White, emissive: true);
        var disk = new ShaderMaterial { Shader = _stellarShader };
        disk.SetShaderParameter("photosphere", new Color(.8f, .87f, 1));
        companion.MaterialOverride = disk;
        companion.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        var glow = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/stellar_glare.gdshader"), RenderPriority = -1 };
        glow.SetShaderParameter("glare_color", new Vector3(.8f, .87f, 1));
        _companionGlare = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new(2, 2) }, MaterialOverride = glow, Visible = false,
            ExtraCullMargin = 20_000, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_companionGlare);
        return companion;
    }

    private ObjectId? PickCompactObject(Vector2 point)
    {
        if (_compactFocus is not { } focus || _navigationCamera.IsPositionBehind(focus.Center))
            return null;
        float angular = focus.Radius / Math.Max(focus.Radius, focus.Center.DistanceTo(_navigationCamera.Position));
        float shadow = (float)SchwarzschildLens.CriticalImpactParameter * angular *
            _navigationCamera.GetViewport().GetVisibleRect().Size.Y / (2 * MathF.Tan(_navigationCamera.Fov * MathF.PI / 360));
        return point.DistanceTo(_navigationCamera.UnprojectPosition(focus.Center)) <= Math.Max(6, shadow) ? focus.Id : null;
    }

    private void HideCompactObjects()
    {
        _compactFocus = null;
        _compactCompanion?.Hide();
        _companionGlare?.Hide();
        _compactLens?.Hide();
    }
}
