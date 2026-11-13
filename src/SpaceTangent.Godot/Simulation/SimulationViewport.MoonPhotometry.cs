// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<ObjectId, MeshInstance3D> _moonPoints = new();
    private ArrayMesh? _moonPointMesh;
    private Shader? _moonPointShader;

    internal MeshInstance3D? MoonPoint(ObjectId id) => _moonPoints.GetValueOrDefault(id);

    private bool DrawMoonPhotometry(NaturalBodyDefinition body, WorldSnapshot snapshot, Vector3d towardEye,
        Vector3d bodyPosition, Vector3 position, float projectedRadius)
    {
        const double au = 149_597_870.7;
        const double sunMagnitudeAtAu = -26.74;
        const double pointPixels = 1, blendPixels = 4, faintestMagnitude = 7.8;

        double range = towardEye.Length;
        double altitude = range - body.RadiusKilometers;
        if (altitude <= 0 || snapshot.SolarSystem is not { } solar)
            return true;

        float height = GetViewport().GetVisibleRect().Size.Y;
        // New split views can draw before their container has assigned a viewport size.
        if (height < 32)
            return true;
        double focalPixels = height / (2 * Math.Tan(Math.PI * _navigationCamera.Fov / 360));
        double disc = focalPixels * body.RadiusKilometers / altitude;
        if (disc >= blendPixels)
            return true;

        Vector3d towardSun = solar.Sun.PositionKilometers - bodyPosition;
        double sunRange = towardSun.Length;
        if (sunRange <= 0)
            return disc >= pointPixels;
        Vector3d sunDirection = towardSun / sunRange;
        Vector3d eyeDirection = towardEye / range;
        double phase = Math.Clamp((1 + Vector3d.Dot(sunDirection, eyeDirection)) / 2, 0, 1);
        if (snapshot.TryState(body.Parent, out var parent))
        {
            Vector3d parentOffset = bodyPosition - parent.PositionKilometers;
            double alongSun = Vector3d.Dot(parentOffset, sunDirection);
            double parentRadius = NavigationCamera.BodyRadiusKilometers(body.Parent);
            if (alongSun < 0 && (parentOffset - sunDirection * alongSun).Length < parentRadius)
                phase = 0;
        }
        double ratio = body.RadiusKilometers / sunRange * (au / range);
        double flux = MoonAlbedo(body.NaifId) * phase * ratio * ratio;
        double magnitude = sunMagnitudeAtAu - 2.5 * Math.Log10(flux);
        if (magnitude < faintestMagnitude)
        {
            MeshInstance3D point = GetMoonPoint(body);
            var material = (ShaderMaterial)point.MaterialOverride;
            material.SetShaderParameter("magnitude", (float)magnitude);
            material.SetShaderParameter("fade", (float)Math.Min(1, (blendPixels - disc) / (blendPixels - pointPixels)));
            Vector3 toCamera = _navigationCamera.Position - position;
            point.Position = position + toCamera.Normalized() * Math.Min(toCamera.Length() * .9f, 1.01f * projectedRadius);
            point.Show();
        }
        return disc >= pointPixels;
    }

    private MeshInstance3D GetMoonPoint(NaturalBodyDefinition body)
    {
        if (_moonPoints.TryGetValue(body.Id, out var existing))
            return existing;
        if (_moonPointMesh is null)
        {
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero };
            _moonPointMesh = new ArrayMesh();
            _moonPointMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arrays);
            _moonPointShader = GD.Load<Shader>("res://Shaders/moon_photometric_point.gdshader");
        }
        var material = new ShaderMaterial { Shader = _moonPointShader };
        Color color = new(body.Color);
        material.SetShaderParameter("point_color", new Vector3(color.R, color.G, color.B));
        var point = new MeshInstance3D
        {
            Mesh = _moonPointMesh, MaterialOverride = material, Visible = false,
            CustomAabb = new Aabb(-Vector3.One, Vector3.One * 2),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _moonPoints.Add(body.Id, point);
        AddChild(point);
        return point;
    }

    // Geometric albedos from the archived built-in solar system catalogue.
    private static double MoonAlbedo(int naif) => naif switch
    {
        401 => .0683, 402 => .068,
        501 => .63, 502 => .67, 503 => .43, 504 => .22,
        601 => .962, 602 => 1.375, 603 => 1.229, 604 => .998, 605 => .949,
        606 => .21, 607 => .44, 608 => .27, 609 => .081,
        701 => .53, 702 => .26, 703 => .35, 704 => .31, 705 => .32,
        801 => .719, 802 => .25, 808 => .096,
        _ => .1
    };
}
