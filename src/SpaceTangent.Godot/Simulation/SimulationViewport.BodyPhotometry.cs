// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<ObjectId, MeshInstance3D> _bodyPoints = new();
    private ArrayMesh? _bodyPointMesh;
    private Shader? _bodyPointShader;

    internal MeshInstance3D? BodyPoint(ObjectId id) => _bodyPoints.GetValueOrDefault(id);

    private bool DrawReflectedBody(ObjectId id, ObjectId parentId, double radius, double albedo, Color tint, WorldSnapshot snapshot, Vector3d towardEye,
        Vector3d bodyPosition, Vector3 position, float projectedRadius, IReadOnlyList<StellarEntry>? lights = null)
    {
        const double au = 149_597_870.7;
        const double sunMagnitudeAtAu = -26.74;
        const double pointPixels = 1, blendPixels = 4, faintestMagnitude = 7.8;

        double range = towardEye.Length;
        double altitude = range - radius;
        if (altitude <= 0 || snapshot.SolarSystem is not { } solar)
            return true;

        float height = GetViewport().GetVisibleRect().Size.Y;
        // New split views can draw before their container has assigned a viewport size.
        if (height < 32)
            return true;
        double focalPixels = height / (2 * Math.Tan(Math.PI * _navigationCamera.Fov / 360));
        double disc = focalPixels * radius / altitude;
        if (disc >= blendPixels)
            return true;

        Vector3d eyeDirection = towardEye / range;
        double flux;
        if (lights is null)
            flux = ReflectedFlux(solar.Sun.PositionKilometers - bodyPosition, 1, SolarSystemSample.SunId);
        else if (CatalogueWorld is { } world && lights.Count > 0)
        {
            flux = 0;
            foreach (var star in lights)
            {
                double absolute = star.AbsoluteMagnitude ?? star.VisualMagnitude - 5 * Math.Log10(star.DistanceParsecs / 10);
                double luminosity = Math.Pow(10, .4 * (4.83 - absolute));
                Vector3d direction = (world.StellarOrigin - bodyPosition) + star.StateAt(snapshot.Epoch).PositionKilometers;
                flux += ReflectedFlux(direction, luminosity, star.Id);
            }
        }
        else
            return true;

        double magnitude = sunMagnitudeAtAu - 2.5 * Math.Log10(flux);
        if (magnitude < faintestMagnitude)
        {
            MeshInstance3D point = GetBodyPoint(id);
            var material = (ShaderMaterial)point.MaterialOverride;
            material.SetShaderParameter("magnitude", (float)magnitude);
            material.SetShaderParameter("point_color", new Vector3(tint.R, tint.G, tint.B));
            material.SetShaderParameter("fade", (float)Math.Min(1, (blendPixels - disc) / (blendPixels - pointPixels)));
            Vector3 toCamera = _navigationCamera.Position - position;
            point.Position = position + toCamera.Normalized() * Math.Min(toCamera.Length() * .9f, 1.01f * projectedRadius);
            point.Show();
        }
        return disc >= pointPixels;

        double ReflectedFlux(Vector3d towardSun, double luminosity, ObjectId lightId)
        {
            double sunRange = towardSun.Length;
            if (sunRange <= 0)
                return 0;
            Vector3d sunDirection = towardSun / sunRange;
            double phase = Math.Clamp((1 + Vector3d.Dot(sunDirection, eyeDirection)) / 2, 0, 1);
            if (parentId != lightId && CatalogueWorld?.FindStellarTarget(parentId) is null &&
                snapshot.TryState(parentId, out var parent))
            {
                Vector3d parentOffset = bodyPosition - parent.PositionKilometers;
                double alongSun = Vector3d.Dot(parentOffset, sunDirection);
                double parentRadius = CatalogueWorld?.TargetRadiusKilometers(parentId) ??
                    NavigationCamera.BodyRadiusKilometers(parentId);
                if (alongSun < 0 && (parentOffset - sunDirection * alongSun).Length < parentRadius)
                    return 0;
            }
            double ratio = radius / sunRange * (au / range);
            return albedo * phase * ratio * ratio * luminosity;
        }
    }

    private MeshInstance3D GetBodyPoint(ObjectId id)
    {
        if (_bodyPoints.TryGetValue(id, out var existing))
            return existing;
        if (_bodyPointMesh is null)
        {
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero };
            _bodyPointMesh = new ArrayMesh();
            _bodyPointMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
            _bodyPointShader = GD.Load<Shader>("res://Shaders/body_photometric_point.gdshader");
        }
        var material = new ShaderMaterial { Shader = _bodyPointShader };
        var point = new MeshInstance3D
        {
            Mesh = _bodyPointMesh, MaterialOverride = material, Visible = false,
            CustomAabb = new Aabb(-Vector3.One, Vector3.One * 2),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _bodyPoints.Add(id, point);
        AddChild(point);
        return point;
    }

    // Geometric albedos from the archived built-in solar system catalogue.
    private static double PlanetAlbedo(int naif) => naif switch
    {
        199 => .14138, 299 => .672604, 399 => .449576, 499 => .174821,
        599 => .510901, 699 => .49974, 799 => .437118, 899 => .409338,
        _ => .1
    };
    private static double NaturalBodyAlbedo(int naif) => naif switch
    {
        401 => .0683, 402 => .068,
        501 => .63, 502 => .67, 503 => .43, 504 => .22,
        601 => .962, 602 => 1.375, 603 => 1.229, 604 => .998, 605 => .949,
        606 => .21, 607 => .44, 608 => .27, 609 => .081,
        701 => .53, 702 => .26, 703 => .35, 704 => .31, 705 => .32,
        801 => .719, 802 => .25, 808 => .096,
        999 => .506, 2000001 => .096, 2000004 => .38, 1000012 => .065,
        _ => .1
    };
}
