// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MeshInstance3D _stellarTarget = null!, _stellarField = null!;
    private IReadOnlyList<StellarEntry> _fieldStars = [];
    private Vector3d? _lastStellarPosition;
    private JulianInstant _lastStellarEpoch;
    private ulong _nextStellarRefresh;

    internal SimulationWorld? CatalogueWorld { get; private set; }
    internal bool StellarDiskResolved { get; private set; }
    internal bool SpatialStarFieldVisible => _stellarField.Visible;

    internal void SetCatalogueWorld(SimulationWorld world)
    {
        if (!ReferenceEquals(CatalogueWorld?.Stellar, world.Stellar))
        {
            _fieldStars = world.Stellar.Stars.Where(star => star.VisualMagnitude <= 8 || star.DistanceParsecs <= 24).ToArray();
            _lastStellarPosition = null;
        }
        if (!ReferenceEquals(CatalogueWorld?.DeepSky, world.DeepSky) || CatalogueWorld?.StellarOrigin != world.StellarOrigin)
            CacheDeepSky(world);
        CatalogueWorld = world;
    }

    private void InitializeStellarRendering()
    {
        _stellarTarget = CreateNavigationSphere(Colors.White, emissive: true);
        _stellarTarget.MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/stellar_disk.gdshader") };
        _stellarField = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_stellarField);
    }

    private void DrawStellarObjects(NavigationCamera pose, WorldSnapshot snapshot, SkyLayerSettings sky,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideStellarObjects();
        if (CatalogueWorld is not { } world)
            return;
        if (pose.Target is { } starId && world.Stellar.Find(starId) is { } star &&
            world.TryNavigationState(starId, snapshot, out CartesianState state))
        {
            var (center, projection) = project(state.PositionKilometers);
            float radius = (float)(star.RadiusKilometers * projection / units);
            float distance = center.DistanceTo(_navigationCamera.Position);
            StellarDiskResolved = radius > distance * .0025f;
            _stellarTarget.Position = center;
            _stellarTarget.Scale = Vector3.One * Math.Max(radius, distance * .0019f);
            ((ShaderMaterial)_stellarTarget.MaterialOverride).SetShaderParameter("photosphere", StarColor(star.ColorIndex));
            _stellarTarget.Show();
        }
        Vector3 localEye = _navigationCamera.Position;
        Vector3d eye = pose.AnchorKilometers + new Vector3d(localEye.X, -localEye.Z, localEye.Y) * units;
        DrawDeepSky(world, pose, sky, eye, units, project);
        if (sky.Stars && _fieldStars.Count != 0 && (eye - world.StellarOrigin).Length > StellarEntry.KilometersPerLightYear * .025)
            DrawSpatialField(eye, snapshot.Epoch, world.StellarOrigin, pose.Target);
    }

    private void DrawSpatialField(Vector3d eye, JulianInstant epoch, Vector3d origin, ObjectId? excluded)
    {
        const double fieldRadius = 6500;
        ulong now = Time.GetTicksMsec();
        bool stale = _lastStellarPosition is not { } previous ||
            (eye - previous).Length > StellarEntry.KilometersPerLightYear * .075 ||
            Math.Abs((double)epoch.DifferenceNanoseconds(_lastStellarEpoch)) > 31_557_600d * JulianInstant.NanosecondsPerSecond;
        if (stale && (now >= _nextStellarRefresh || _lastStellarPosition is null))
        {
            _lastStellarPosition = eye;
            _lastStellarEpoch = epoch;
            _nextStellarRefresh = now + 110;
            var vertices = new List<Vector3>(_fieldStars.Count);
            var colors = new List<Color>(_fieldStars.Count);
            foreach (StellarEntry star in _fieldStars)
            {
                if (star.Id == excluded)
                    continue;
                Vector3d relative = star.StateAt(epoch).PositionKilometers + origin - eye;
                double distance = relative.Length;
                if (distance < 1000)
                    continue;
                double magnitude = star.VisualMagnitude +
                    5 * Math.Log10(distance / (star.DistanceParsecs * StellarEntry.KilometersPerParsec));
                if (magnitude > 8)
                    continue;
                vertices.Add(ToGodot(relative, distance / fieldRadius));
                Color color = StarColor(star.ColorIndex);
                color.A = (float)Math.Clamp((8 - magnitude) / 10, .07, 1);
                colors.Add(color);
            }
            _stellarField.Mesh = null;
            if (vertices.Count > 0)
            {
                var arrays = new global::Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
                arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
                var mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
                mesh.SurfaceSetMaterial(0, new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/spatial_star_field.gdshader") });
                _stellarField.Mesh = mesh;
            }
        }
        _stellarField.Position = _navigationCamera.Position;
        _stellarField.Show();
    }

    private static Color StarColor(double colorIndex)
    {
        if (colorIndex < .65)
            return new Color("b7d0ff").Lerp(new Color("fff1ce"), (float)Math.Clamp((colorIndex + .4) / 1.05, 0, 1));
        return new Color("fff1ce").Lerp(new Color("ff9c68"), (float)Math.Clamp((colorIndex - .65) / 1.4, 0, 1));
    }

    private void HideStellarObjects()
    {
        _stellarTarget.Hide();
        _stellarField.Hide();
        StellarDiskResolved = false;
        HideDeepSky();
    }
}
