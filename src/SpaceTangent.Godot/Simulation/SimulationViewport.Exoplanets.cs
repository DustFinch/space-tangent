// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly List<MeshInstance3D> _exoplanetGlobePool = [];
    private readonly Dictionary<ObjectId, MeshInstance3D> _visibleExoplanets = new();
    private IReadOnlyDictionary<string, Texture2D> _exoplanetTextures = new Dictionary<string, Texture2D>();
    private Shader? _exoplanetGlobeShader;

    internal void SetExoplanetTextures(IReadOnlyDictionary<string, Texture2D> textures) => _exoplanetTextures = textures;
    internal bool ExoplanetVisible(ObjectId id) => _visibleExoplanets.ContainsKey(id);
    internal bool ExoplanetTextured(ObjectId id) => _visibleExoplanets.GetValueOrDefault(id)?.MaterialOverride is ShaderMaterial material &&
        material.GetShaderParameter("has_globe_map").AsBool();

    private void DrawExoplanets(SimulationWorld world, NavigationCamera pose, WorldSnapshot snapshot,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        if (pose.Target is not { } target)
            return;
        ObjectId host = world.Exoplanets.Find(target)?.Host ?? target;
        if (world.Exoplanets.FindHost(host) is null || !world.TryNavigationState(host, snapshot, out var hostState))
            return;
        foreach (ExoplanetEntry planet in world.Exoplanets.BodiesOf(host))
        {
            if (_visibleExoplanets.Count >= 32 || !world.TryNavigationState(planet.Id, snapshot, out var state))
                continue;
            var (center, projection) = project(state.PositionKilometers);
            float radius = (float)(planet.RadiusKilometers * projection / units);
            if (planet.Id != target && (radius < .03f || _navigationCamera.IsPositionBehind(center)))
                continue;
            Vector3d toLight = (hostState - state).PositionKilometers;
            if (!(toLight.Length > 0))
                continue;
            int index = _visibleExoplanets.Count;
            if (_exoplanetGlobePool.Count == index)
                _exoplanetGlobePool.Add(CreateExoplanetGlobe());
            MeshInstance3D globe = _exoplanetGlobePool[index];
            Basis3d orientation = planet.BodyToIcrf(snapshot.Epoch);
            globe.Transform = new(PlanetBasis(orientation) * Basis.FromScale(Vector3.One * radius), center);
            var material = (ShaderMaterial)globe.MaterialOverride;
            var map = _exoplanetTextures.GetValueOrDefault(planet.TextureClass);
            material.SetShaderParameter("globe_map", map is null ? default : (Variant)map);
            material.SetShaderParameter("has_globe_map", map is not null);
            material.SetShaderParameter("globe_color", SurfaceColor(planet.TextureClass));
            material.SetShaderParameter("prime_meridian_offset", .25f);
            material.SetShaderParameter("light_in_body", ToGodot(orientation.Transpose().Transform(toLight / toLight.Length), 1));
            globe.Show();
            _visibleExoplanets.Add(planet.Id, globe);
        }
    }

    private MeshInstance3D CreateExoplanetGlobe()
    {
        var globe = CreateNavigationSphere(Colors.White);
        _exoplanetGlobeShader ??= GD.Load<Shader>("res://Shaders/world_orbit_globe.gdshader");
        globe.MaterialOverride = new ShaderMaterial { Shader = _exoplanetGlobeShader };
        return globe;
    }

    private void HideExoplanets()
    {
        _visibleExoplanets.Clear();
        foreach (var globe in _exoplanetGlobePool)
            globe.Hide();
    }

    private static Color SurfaceColor(string texture) => new(texture switch
    {
        "rocky" => "937c68", "venuslike" => "d8bb84", "icy" => "adb9c6", "neptunelike" => "879db8",
        "exo-class1" => "d6bea3", "exo-class2" => "c4a585", "exo-class3" => "aaa7b8",
        "exo-class4" => "b88864", "exo-class5" => "d8ae90", _ => "9f9b91"
    });
}
