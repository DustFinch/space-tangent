// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const int MaximumSystemDisks = 64;
    private MeshInstance3D _stellarField = null!;
    private Shader _stellarShader = null!;
    private readonly List<MeshInstance3D> _stellarDisks = [];
    private readonly Dictionary<ObjectId, MeshInstance3D> _visibleStars = new();
    private ILookup<ObjectId, StellarEntry> _starSystems = Array.Empty<StellarEntry>().ToLookup(star => star.Id);
    private ObjectId? _lastStellarTarget;
    private IReadOnlyList<StellarEntry> _fieldStars = [];
    private Vector3d? _lastStellarPosition;
    private JulianInstant _lastStellarEpoch;
    private ulong _nextStellarRefresh;

    internal SimulationWorld? CatalogueWorld { get; private set; }
    internal bool StellarDiskResolved { get; private set; }
    internal int VisibleStellarDisks => _visibleStars.Count;
    internal bool SpatialStarFieldVisible => _stellarField.Visible;

    internal void SetCatalogueWorld(SimulationWorld world)
    {
        if (!ReferenceEquals(CatalogueWorld?.Stellar, world.Stellar))
        {
            _fieldStars = world.Stellar.Stars.Where(star => !star.IsBarycenter && star.RadiusKilometers > 0 &&
                (star.VisualMagnitude <= 8 || star.DistanceParsecs <= 24)).ToArray();
            _starSystems = world.Stellar.Stars.Where(star => star.BarycenterId.HasValue).ToLookup(star => star.BarycenterId!.Value);
            _lastStellarPosition = null;
        }
        if (!ReferenceEquals(CatalogueWorld?.DeepSky, world.DeepSky) || CatalogueWorld?.StellarOrigin != world.StellarOrigin)
            CacheDeepSky(world);
        CatalogueWorld = world;
    }

    private void InitializeStellarRendering()
    {
        _stellarShader = GD.Load<Shader>("res://Shaders/stellar_disk.gdshader");
        _stellarField = new MeshInstance3D { Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_stellarField);
    }

    private void DrawStellarObjects(NavigationCamera pose, WorldSnapshot snapshot, SkyLayerSettings sky,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        HideStellarObjects();
        if (CatalogueWorld is not { } world)
            return;
        if (pose.Target is { } starId && world.FindStellarTarget(world.Exoplanets.Find(starId)?.Host ?? starId) is { } star)
            DrawStellarSystem(star, world, snapshot, units, project);
        Vector3 localEye = _navigationCamera.Position;
        Vector3d eye = pose.AnchorKilometers + new Vector3d(localEye.X, -localEye.Z, localEye.Y) * units;
        DrawDeepSky(world, pose, sky, eye, units, project);
        DrawExoplanets(world, pose, snapshot, units, project);
        if (sky.Stars && _fieldStars.Count != 0 && (eye - world.StellarOrigin).Length > StellarEntry.KilometersPerLightYear * .025)
            DrawSpatialField(eye, snapshot.Epoch, world.StellarOrigin, pose.Target);
    }

    private void DrawSpatialField(Vector3d eye, JulianInstant epoch, Vector3d origin, ObjectId? excluded)
    {
        const double fieldRadius = 6500;
        ulong now = Time.GetTicksMsec();
        bool stale = _lastStellarTarget != excluded || _lastStellarPosition is not { } previous ||
            (eye - previous).Length > StellarEntry.KilometersPerLightYear * .075 ||
            Math.Abs((double)epoch.DifferenceNanoseconds(_lastStellarEpoch)) > 31_557_600d * JulianInstant.NanosecondsPerSecond;
        if (stale && (now >= _nextStellarRefresh || _lastStellarPosition is null))
        {
            _lastStellarTarget = excluded;
            _lastStellarPosition = eye;
            _lastStellarEpoch = epoch;
            _nextStellarRefresh = now + 110;
            var vertices = new List<Vector3>(_fieldStars.Count);
            var colors = new List<Color>(_fieldStars.Count);
            foreach (StellarEntry star in _fieldStars)
            {
                if (_visibleStars.ContainsKey(star.Id))
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
                Color color = StarColor(star);
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

    private void DrawStellarSystem(StellarEntry target, SimulationWorld world, WorldSnapshot snapshot,
        double units, Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        Draw(target);
        StellarEntry root = target;
        while (root.BarycenterId is { } id && world.Stellar.Find(id) is { } parent)
            root = parent;
        DrawSystem(root);

        void DrawSystem(StellarEntry primary)
        {
            Draw(primary);
            foreach (StellarEntry star in _starSystems[primary.Id])
            {
                if (_visibleStars.Count == MaximumSystemDisks)
                    break;
                DrawSystem(star);
            }
        }

        void Draw(StellarEntry star)
        {
            if (star.IsBarycenter || star.RadiusKilometers <= 0 || _visibleStars.ContainsKey(star.Id) ||
                !world.TryNavigationState(star.Id, snapshot, out CartesianState state))
                return;
            var (center, projection) = project(state.PositionKilometers);
            if (_navigationCamera.IsPositionBehind(center))
                return;
            float radius = (float)(star.RadiusKilometers * projection / units);
            float distance = center.DistanceTo(_navigationCamera.Position);
            if (star == target)
                StellarDiskResolved = radius > distance * .0025f;
            int index = _visibleStars.Count;
            if (_stellarDisks.Count == index)
            {
                var created = CreateNavigationSphere(Colors.White, emissive: true);
                created.MaterialOverride = new ShaderMaterial { Shader = _stellarShader };
                _stellarDisks.Add(created);
            }
            var disk = _stellarDisks[index];
            disk.Position = center;
            disk.Scale = Vector3.One * Math.Max(radius, distance * .0019f);
            ((ShaderMaterial)disk.MaterialOverride).SetShaderParameter("photosphere", StarColor(star));
            disk.Show();
            _visibleStars.Add(star.Id, disk);
        }
    }

    private static Color StarColor(StellarEntry star) => star.ColorSrgb is { } color
        ? new((float)color.X, (float)color.Y, (float)color.Z) : StarColor(star.ColorIndex);

    private static Color StarColor(double colorIndex)
    {
        if (colorIndex < .65)
            return new Color("b7d0ff").Lerp(new Color("fff1ce"), (float)Math.Clamp((colorIndex + .4) / 1.05, 0, 1));
        return new Color("fff1ce").Lerp(new Color("ff9c68"), (float)Math.Clamp((colorIndex - .65) / 1.4, 0, 1));
    }

    private void HideStellarObjects()
    {
        foreach (MeshInstance3D disk in _stellarDisks)
            disk.Hide();
        _visibleStars.Clear();
        _stellarField.Hide();
        StellarDiskResolved = false;
        HideDeepSky();
        HideExoplanets();
    }
}
