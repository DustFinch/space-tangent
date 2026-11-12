// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal sealed class CompactLensRenderer
{
    private const int FaceSize = 1024;
    private const float Extent = 1 + 4f / FaceSize;
    private const uint StarLayer = 2;
    private static readonly (Vector3 Forward, Vector3 Up)[] Directions =
    [
        (Vector3.Right, Vector3.Up), (Vector3.Left, Vector3.Up), (Vector3.Up, Vector3.Back),
        (Vector3.Down, Vector3.Forward), (Vector3.Back, Vector3.Up), (Vector3.Forward, Vector3.Up)
    ];

    private sealed record Face(SubViewport Viewport, Camera3D Camera, BrightSkyOverlay Sky,
        ColorRect Galaxy, ShaderMaterial GalacticMaterial, Vector3 Forward, Vector3 Up, Vector3 Right);

    private readonly MeshInstance3D _mesh;
    private readonly ShaderMaterial _material;
    private readonly Face[] _faces;
    private (JulianInstant Epoch, SkyLayerSettings Sky, Texture2D? MilkyWay, bool Spatial)? _captured;

    private ulong _lastCaptureMilliseconds;

    internal CompactLensRenderer(SimulationViewport parent, BrightSkyData data, CompactLensTables tables)
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/compact_object_lens.gdshader"), RenderPriority = 100 };
        _material.SetShaderParameter("face_extent", Extent);
        _material.SetShaderParameter("deflection_table", tables.Angles);
        _material.SetShaderParameter("color_table", tables.Spectrum);
        using var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-1, -1, 0), new Vector3(3, -1, 0), new Vector3(-1, 3, 0) };
        var fullScreen = new ArrayMesh();
        fullScreen.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _mesh = new MeshInstance3D
        {
            Mesh = fullScreen, MaterialOverride = _material, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new(-Vector3.One * 2, Vector3.One * 4), ExtraCullMargin = 20_000
        };
        parent.AddChild(_mesh);

        var environment = new global::Godot.Environment
        {
            BackgroundMode = global::Godot.Environment.BGMode.Canvas, BackgroundCanvasMaxLayer = 0
        };
        _faces = new Face[Directions.Length];
        for (int index = 0; index < _faces.Length; index++)
        {
            var (forward, up) = Directions[index];
            Vector3 right = forward.Cross(up);
            var viewport = new SubViewport
            {
                Size = new(FaceSize, FaceSize), World3D = parent.GetViewport().FindWorld3D(),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
            };
            parent.AddChild(viewport);
            var camera = new Camera3D
            {
                Current = true, CullMask = StarLayer, Environment = environment, Near = .02f, Far = 12_000,
                Fov = (float)(2 * Math.Atan(Extent) * 180 / Math.PI), KeepAspect = Camera3D.KeepAspectEnum.Height,
                Basis = new(right, up, -forward)
            };
            viewport.AddChild(camera);
            var fill = new ColorRect { Color = new(.01f, .012f, .017f), MouseFilter = Control.MouseFilterEnum.Ignore };
            viewport.AddChild(fill);
            fill.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var galacticMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/galactic_background.gdshader") };
            SimulationViewport.ConfigureGalacticAxes(galacticMaterial);
            galacticMaterial.SetShaderParameter("camera_forward", Equatorial(forward));
            galacticMaterial.SetShaderParameter("camera_right", Equatorial(right));
            galacticMaterial.SetShaderParameter("camera_up", Equatorial(up));
            galacticMaterial.SetShaderParameter("half_field", Extent);
            var galaxy = new ColorRect { Material = galacticMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
            viewport.AddChild(galaxy);
            galaxy.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var overlay = new BrightSkyOverlay();
            overlay.Configure(data, fillBackground: false);
            viewport.AddChild(overlay);
            overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _faces[index] = new(viewport, camera, overlay, galaxy, galacticMaterial, forward, up, right);
            _material.SetShaderParameter("face" + index, viewport.GetTexture());
        }
    }

    internal bool Visible => _mesh.Visible;
    internal int Captures { get; private set; }
    internal void Hide() => _mesh.Hide();

    internal void Draw(Camera3D camera, BlackHole hole, Vector3 center, float radius,
        JulianInstant epoch, SkyLayerSettings sky, Texture2D? milkyWay, bool spatial, bool cinematic,
        Vector3? companionCenter, float companionRadius, bool diskEmission)
    {
        var key = (epoch, sky, milkyWay, spatial);
        ulong now = Time.GetTicksMsec();
        bool refresh = _captured is not { } previous || !Visible || previous.Sky != sky ||
            previous.MilkyWay != milkyWay || previous.Spatial != spatial ||
            Math.Abs((double)epoch.DifferenceNanoseconds(previous.Epoch)) >= JulianInstant.NanosecondsPerDay ||
            previous.Epoch != epoch && now - _lastCaptureMilliseconds >= 250;
        if (refresh)
        {
            foreach (Face face in _faces)
            {
                face.Camera.Position = camera.Position;
                face.Galaxy.Visible = milkyWay is not null;
                if (milkyWay is not null)
                    face.GalacticMaterial.SetShaderParameter("galactic_image", milkyWay);
                face.Sky.ShowFrame(new(epoch, sky, null, Equatorial(face.Forward), Equatorial(face.Right),
                    Equatorial(face.Up), Extent, ShowCatalogue: !spatial && sky.Stars));
                face.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            }
            _captured = key;
            _lastCaptureMilliseconds = now;
            Captures++;
        }

        Basis inverse = camera.GlobalBasis.Transposed();
        _material.SetShaderParameter("lens_position", inverse * (center - camera.GlobalPosition));
        _material.SetShaderParameter("lens_radius", radius);
        _material.SetShaderParameter("camera_to_sky", camera.GlobalBasis);
        _material.SetShaderParameter("clip_near", camera.Near);
        _material.SetShaderParameter("clip_far", camera.Far);
        _material.SetShaderParameter("pixel_angle", 2 * MathF.Tan(camera.Fov * MathF.PI / 360) /
            Math.Max(1, camera.GetViewport().GetVisibleRect().Size.Y));

        Vector3d? diskAxis = hole.DiskAxis();
        bool disk = diskAxis.HasValue;
        Vector3 axis = disk ? SceneAxis(diskAxis.GetValueOrDefault()) : Vector3.Zero;
        _material.SetShaderParameter("disk_normal", inverse * axis);
        _material.SetShaderParameter("hole_spin", disk ? (float)hole.Spin : 0);
        if (disk)
        {
            var reference = axis.Cross(Vector3.Up);
            if (reference.LengthSquared() < 1e-6f)
                reference = axis.Cross(Vector3.Right);
            _material.SetShaderParameter("disk_reference", inverse * reference.Normalized());
            _material.SetShaderParameter("disk_outer", 30f);
            _material.SetShaderParameter("disk_temperature", cinematic ? 5000f : PeakTemperature(hole));
            _material.SetShaderParameter("disk_cinematic", cinematic ? 1f : 0f);
            _material.SetShaderParameter("disk_exposure", diskEmission ? (cinematic ? 1.3f : .4f) : 0);
        }

        _material.SetShaderParameter("glare_strength", companionCenter.HasValue ? 1f : 0f);
        if (companionCenter is { } star)
        {
            _material.SetShaderParameter("glare_position", inverse * (star - camera.GlobalPosition));
            _material.SetShaderParameter("glare_radius", companionRadius);
            _material.SetShaderParameter("glare_color", new Vector3(.8f, .87f, 1));
        }
        _mesh.Show();
    }

    private static Vector3 Equatorial(Vector3 value) => new(value.X, -value.Z, value.Y);
    private static Vector3 SceneAxis(Vector3d value) => new((float)value.X, (float)value.Z, (float)-value.Y);

    private static float PeakTemperature(BlackHole hole)
    {
        const double speed = 299_792_458, protonMass = 1.67262192369e-27, thomson = 6.6524587321e-29;
        const double stefanBoltzmann = 5.670374419e-8;
        double gm = 1.3271244e20 * hole.MassSolar;
        double eddington = 4 * Math.PI * gm * protonMass * speed / thomson;
        double accretion = .1 * eddington / (KerrLens.Efficiency(hole.Spin) * speed * speed);
        double inner = KerrLens.Isco(hole.Spin) * gm / (speed * speed);
        double peak = 49d / 36 * inner;
        double flux = 3 * gm * accretion * (1 - Math.Sqrt(inner / peak)) / (8 * Math.PI * peak * peak * peak);
        return (float)Math.Pow(flux / stefanBoltzmann, .25);
    }
}
