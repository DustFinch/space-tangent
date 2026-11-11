// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Simulation;

internal sealed record SurfaceTextures(Texture2D Clouds, Texture2D Moon);

internal sealed class SurfaceProjection
{
    internal static readonly JulianInstant FullMoonEpoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
    private static readonly EarthSite Site = new(55.7558, 37.6173, .15);
    private const double Degrees = Math.PI / 180;
    private const double Heading = 237 * Degrees;
    private readonly ColorRect _surface;
    private readonly ShaderMaterial _material;
    internal double PitchDegrees { get; set; } = 28;

    internal SurfaceProjection(Node owner, SurfaceTextures textures)
    {
        var layer = new CanvasLayer();
        owner.AddChild(layer);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_observer.gdshader") };
        _material.SetShaderParameter("cloud_texture", textures.Clouds);
        _material.SetShaderParameter("moon_texture", textures.Moon);
        _surface = new ColorRect { Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        layer.AddChild(_surface);
        _surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    internal bool Draw(bool surface, SolarSystemSample sample, DeltaTModel model)
    {
        _surface.Visible = surface;
        if (!surface)
            return true;

        SurfaceLighting lighting;
        try
        {
            lighting = EarthSurfaceLighting.Sample(sample, Site, model);
        }
        catch (ArgumentOutOfRangeException)
        {
            _surface.Hide();
            return false;
        }

        Vector3 east = Convert(lighting.EastNorthUp.X);
        Vector3 north = Convert(lighting.EastNorthUp.Y);
        Vector3 up = Convert(lighting.EastNorthUp.Z);
        Vector3 horizontal = north * (float)Math.Cos(Heading) + east * (float)Math.Sin(Heading);
        double pitch = PitchDegrees * Degrees;
        Vector3 forward = horizontal * (float)Math.Cos(pitch) + up * (float)Math.Sin(pitch);
        Vector3 right = forward.Cross(up).Normalized();
        Vector3 cameraUp = right.Cross(forward);
        _material.SetShaderParameter("observer", Convert(lighting.PositionKilometers));
        _material.SetShaderParameter("forward", forward);
        _material.SetShaderParameter("right", right);
        _material.SetShaderParameter("camera_up", cameraUp);
        _material.SetShaderParameter("earth_to_sun", Convert(lighting.EarthToSun));
        _material.SetShaderParameter("earth_to_moon", Convert(lighting.EarthToMoon));
        _material.SetShaderParameter("earth_angle", (float)lighting.EarthRotationRadians);
        _material.SetShaderParameter("earth_radius", (float)EarthSurfaceLighting.EarthRadiusKilometers);
        _material.SetShaderParameter("moon_radius", (float)EarthSurfaceLighting.MoonRadiusKilometers);
        _material.SetShaderParameter("lunar_strength", (float)Math.Clamp(.12 * lighting.RelativeLunarFlux, 0, 1));
        return true;
    }

    internal void SetControls(bool clouds, bool lunarLight)
    {
        _material.SetShaderParameter("show_clouds", clouds);
        _material.SetShaderParameter("lunar_cloud_light", lunarLight);
    }

    internal static SurfaceTextures? LoadTextures()
    {
        string directory = LocalDataPaths.Find("SPACETANGENT_SURFACE_TEXTURES", "surface", directory: true);
        string clouds = Path.Combine(directory, "earth-clouds.png");
        string moon = Path.Combine(directory, "moon.jpg");
        if (!File.Exists(clouds) || !File.Exists(moon))
            return null;
        return new(LoadTexture(clouds), LoadTexture(moon));
    }

    private static Texture2D LoadTexture(string path)
    {
        using Image image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
            throw new InvalidDataException("Could not decode the surface-view texture.");
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private static Vector3 Convert(Vector3d value) => new((float)value.X, (float)value.Y, (float)value.Z);
}
