// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Surfaces;

namespace SpaceTangent.Godot.Simulation;

internal sealed record DecodedBodyTerrain(int Width, int Height, float RadiusKilometers, byte[] Meters)
{
    internal static DecodedBodyTerrain Load(string path, double expectedRadius, CancellationToken cancellation)
    {
        ElevationMap map = ElevationMap.Load(path, cancellation);
        if (Math.Abs(map.ReferenceRadiusKilometers - expectedRadius) > 0.001)
            throw new InvalidDataException("The terrain datum does not match the reference radius.");
        var bytes = new byte[checked(map.Width * map.Height * sizeof(float))];
        map.CopyHeightsMeters(MemoryMarshal.Cast<byte, float>(bytes), cancellation);
        return new(map.Width, map.Height, (float)map.ReferenceRadiusKilometers, bytes);
    }
}

internal sealed record BodyTerrainAsset(Texture2D Heights, float RadiusKilometers, Mesh Globe, Mesh Patch)
{
    internal static BodyTerrainAsset Publish(DecodedBodyTerrain decoded)
    {
        using Image image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rf, decoded.Meters);
        if (image.IsEmpty())
            throw new InvalidDataException("Could not create the terrain height texture.");
        return new(ImageTexture.CreateFromImage(image), decoded.RadiusKilometers,
            new SphereMesh { Radius = 1, Height = 2, RadialSegments = 256, Rings = 128 },
            new PlaneMesh { Size = new(2, 2), SubdivideWidth = 191, SubdivideDepth = 191 });
    }
}

internal sealed class BodyTerrainProjection
{
    private readonly Camera3D _camera;
    private readonly MeshInstance3D _mesh;
    private readonly ShaderMaterial _material;
    private BodyTerrainAsset? _asset;
    private LunarTerrainCamera? _lastView;

    internal BodyTerrainProjection(Node owner, Texture2D? surface = null, BodyTileTextures? tiles = null)
    {
        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Near = 100,
            Far = 8000,
            Environment = new global::Godot.Environment
            {
                BackgroundMode = global::Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("#0b0e17")
            }
        };
        owner.AddChild(_camera);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/lunar_terrain.gdshader") };
        _material.SetShaderParameter("textured_surface", surface is not null);
        if (surface is not null)
            _material.SetShaderParameter("surface_map", surface);
        Tiles = tiles;
        Tiles?.Bind(_material);
        _mesh = new MeshInstance3D { Visible = false, MaterialOverride = _material };
        owner.AddChild(_mesh);
    }

    internal BodyTileTextures? Tiles { get; }
    internal bool Visible => _mesh.Visible;
    internal bool Regional => _asset is not null && _mesh.Mesh == _asset.Patch;
    internal Vector3 SunDirection { get; private set; }

    internal void Replace(BodyTerrainAsset? asset)
    {
        _asset = asset;
        _lastView = null;
        _mesh.Mesh = null;
        _material.SetShaderParameter("heights", asset is null ? default : (Variant)asset.Heights);
        if (asset is null)
        {
            Hide();
            return;
        }

        float radius = asset.RadiusKilometers;
        _material.SetShaderParameter("radius_km", radius);
        var bounds = new Vector3(radius + 50, radius + 50, radius + 50);
        _mesh.CustomAabb = new(-bounds, 2 * bounds);
    }

    internal void Hide() => _mesh.Hide();

    internal void FitCamera(Vector2 size) =>
        _camera.KeepAspect = size.X >= size.Y ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;

    internal bool Draw(LunarTerrainCamera view, Vector3d lightBody, float sunFraction = 1, bool showRelief = true)
    {
        if (_asset is not { } asset)
            return false;
        if (_lastView != view)
        {
            _lastView = view;
            double radians = Math.PI / 180;
            var (sinLon, cosLon) = Math.SinCos(view.LongitudeDegrees * radians);
            var (sinLat, cosLat) = Math.SinCos(view.LatitudeDegrees * radians);
            Vector3 up = ToGodot(new(cosLat * cosLon, cosLat * sinLon, sinLat));
            Vector3 east = ToGodot(new(-sinLon, cosLon, 0));
            Vector3 north = up.Cross(east);
            _camera.Position = up * (asset.RadiusKilometers + 2000);
            _camera.LookAt(Vector3.Zero, north);
            _camera.Size = (float)view.FieldKilometers;
            bool regional = view.FieldKilometers <= 1600;
            _mesh.Mesh = regional ? asset.Patch : asset.Globe;
            _material.SetShaderParameter("regional", regional);
            _material.SetShaderParameter("site_up", up);
            _material.SetShaderParameter("site_east", east);
            _material.SetShaderParameter("site_north", north);
            if (regional)
            {
                double ratio = .6 * view.FieldKilometers / asset.RadiusKilometers;
                _material.SetShaderParameter("patch_span", (float)(ratio / Math.Sqrt(1 - ratio * ratio)));
            }
        }
        SunDirection = ToGodot(lightBody).Normalized();
        _material.SetShaderParameter("sun_direction", SunDirection);
        _material.SetShaderParameter("sun_fraction", sunFraction);
        _material.SetShaderParameter("show_relief", showRelief);
        if (!_camera.Current)
            _camera.MakeCurrent();
        _mesh.Show();
        return true;
    }

    private static Vector3 ToGodot(Vector3d vector) => new((float)vector.X, (float)vector.Z, (float)-vector.Y);
}
