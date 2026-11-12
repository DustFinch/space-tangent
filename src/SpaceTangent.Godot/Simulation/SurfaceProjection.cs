// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal sealed record SurfaceTextures(Texture2D Clouds, Texture2D Moon);

internal sealed partial class SurfaceProjection
{
    internal static readonly JulianInstant FullMoonEpoch = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
    private const double Degrees = Math.PI / 180;
    private readonly ColorRect _surface;
    private readonly ShaderMaterial _material;
    private readonly Label[] _cardinals;
    private BrightSkyOverlay? _sky;
    private Texture2D? _galacticImage;
    internal bool GalacticSkyVisible => _galacticImage is not null && _surface.Visible;
    private MeteorOverlay? _meteors;

    internal bool ShowsMeteorRadiant(string code) => _surface.Visible && _meteors?.ShowsRadiant(code) == true;
    private (Vector3 Forward, Vector3 Right, Vector3 Up, Vector3 North, Vector3 East)? _cardinalFrame;
    private float _tanHalfField = .087488664f;
    internal double? PitchDegrees { get; set; }

    internal SurfaceProjection(Node owner, SurfaceTextures textures)
    {
        var layer = new CanvasLayer();
        owner.AddChild(layer);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/earth_observer.gdshader") };
        SimulationViewport.ConfigureGalacticAxes(_material);
        _material.SetShaderParameter("cloud_texture", textures.Clouds);
        _material.SetShaderParameter("moon_texture", textures.Moon);
        _surface = new ColorRect { Material = _material, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        layer.AddChild(_surface);
        _surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _cardinals = new[] { "N", "E", "S", "W" }.Select(CreateCardinal).ToArray();
        _surface.Resized += PlaceCardinals;
    }

    internal void SetSkyData(BrightSkyData data)
    {
        _sky = new();
        _sky.Configure(data, fillBackground: false);
        _surface.AddChild(_sky);
        _surface.MoveChild(_sky, 0);
        _sky.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _meteors = new();
        _meteors.Configure(data.Meteors);
        _surface.AddChild(_meteors);
        _surface.MoveChild(_meteors, 1);
        _meteors.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    internal void SetGalacticSky(Texture2D? image)
    {
        if (ReferenceEquals(_galacticImage, image))
            return;
        _galacticImage = image;
        if (image is not null)
            _material.SetShaderParameter("galactic_image", image);
        _material.SetShaderParameter("show_galactic_sky", image is not null);
    }

    internal ObjectId? PickStar(Vector2 position) => _sky?.Pick(position);

    internal bool Draw(bool surface, SolarSystemSample sample, DeltaTModel model, EarthObserverCamera? camera = null,
        SkyLayerSettings sky = default, ObjectId? selection = null, double fieldOfView = 10,
        VisualOptics? optics = null, SimulationWorld? world = null, WorldSnapshot? snapshot = null)
    {
        _surface.Visible = surface;
        if (!surface)
        {
            _meteors?.HideFrame();
            return true;
        }

        EarthObserverCamera observer = camera ?? EarthObserverCamera.Moscow;
        SurfaceLighting lighting;
        try
        {
            lighting = EarthSurfaceLighting.Sample(sample, observer.Site, model);
        }
        catch (ArgumentOutOfRangeException)
        {
            _surface.Hide();
            _meteors?.HideFrame();
            return false;
        }

        if (optics is { HoldSelection: true } && selection is { } target && world is not null)
        {
            var horizontalAtTarget = world.BrightStars.Find(target) is { } star
                ? EarthObserving.HorizontalDirection(lighting, star.DirectionAt(sample.Epoch))
                : snapshot is not null && world.TryNavigationState(target, snapshot, out var state, model)
                    ? EarthObserving.Horizontal(lighting, (state - sample.Earth).PositionKilometers) : null;
            if (horizontalAtTarget is { } point)
                observer = observer with
                {
                    AzimuthDegrees = point.AzimuthDegrees ?? observer.AzimuthDegrees,
                    ElevationDegrees = observer.Conditions?.ApparentAltitude(point.AltitudeDegrees, observer.Site) ?? point.AltitudeDegrees
                };
        }
        Vector3 east = Convert(lighting.EastNorthUp.X);
        Vector3 north = Convert(lighting.EastNorthUp.Y);
        Vector3 up = Convert(lighting.EastNorthUp.Z);
        double heading = observer.AzimuthDegrees * Degrees;
        Vector3 horizontal = north * (float)Math.Cos(heading) + east * (float)Math.Sin(heading);
        double pitch = (PitchDegrees ?? observer.ElevationDegrees) * Degrees;
        Vector3 forward = horizontal * (float)Math.Cos(pitch) + up * (float)Math.Sin(pitch);
        Vector3 right = horizontal.Cross(up).Normalized();
        Vector3 cameraUp = right.Cross(forward);
        if (optics?.Inverted == true)
        {
            right = -right;
            cameraUp = -cameraUp;
        }
        _tanHalfField = (float)Math.Tan(fieldOfView * Degrees / 2);
        _material.SetShaderParameter("tan_half_field", _tanHalfField);
        _material.SetShaderParameter("observer", Convert(lighting.PositionKilometers));
        _material.SetShaderParameter("forward", forward);
        _material.SetShaderParameter("right", right);
        _material.SetShaderParameter("camera_up", cameraUp);
        _material.SetShaderParameter("earth_to_sun", Convert(lighting.EarthToSun));
        _material.SetShaderParameter("earth_to_moon", Convert(lighting.EarthToMoon));
        var moonOffset = lighting.EarthToMoon - lighting.PositionKilometers;
        Vector3d moon = moonOffset / moonOffset.Length;
        var moonSeen = observer.Conditions?.RefractDirection(moon, lighting.EastNorthUp.Z, observer.Site) ?? moon;
        _material.SetShaderParameter("moon_seen", Convert(moonSeen));
        _material.SetShaderParameter("sky_glow", (float)(observer.Conditions?.ZenithGlow() ?? 0));
        EclipseRendering.SetEarthShadow(_material, lighting, godotAxes: false);
        _material.SetShaderParameter("earth_angle", (float)lighting.EarthRotationRadians);
        _material.SetShaderParameter("earth_radius", (float)EarthSurfaceLighting.EarthRadiusKilometers);
        _material.SetShaderParameter("moon_radius", (float)EarthSurfaceLighting.MoonRadiusKilometers);
        _material.SetShaderParameter("lunar_strength", (float)Math.Clamp(.12 * lighting.RelativeLunarFlux, 0, 1));
        _material.SetShaderParameter("site_north", north);
        _material.SetShaderParameter("site_east", east);
        _material.SetShaderParameter("show_horizon_grid", observer.HorizonGrid);
        if (_sky is not null && sky.Visible)
        {
            float solarAltitude = (float)(lighting.SolarAltitudeRadians / Degrees);
            float moonRadius = MathF.Asin(MathF.Min(4 * (float)(EarthSurfaceLighting.MoonRadiusKilometers / moonOffset.Length), .1f));
            _sky.ShowFrame(new(sample.Epoch, sky, selection, forward, right, cameraUp, _tanHalfField,
                1 - Mathf.SmoothStep(-12, -4, solarAltitude), up, Convert(moonSeen), moonRadius,
                Conditions: observer.Conditions, Site: observer.Site, OpticalLimit: optics?.LimitingMagnitude()));
        }
        else
            _sky?.Hide();
        if (sky.Meteors)
            _meteors?.ShowFrame(sample, lighting, observer.Site, observer.Conditions?.LimitingMagnitude() ?? 6.5,
                forward, right, cameraUp, _tanHalfField);
        else
            _meteors?.HideFrame();
        DrawLandscape(lighting, forward, right, cameraUp, north, east, up);
        _cardinalFrame = observer.CardinalPoints ? (forward, right, cameraUp, north, east) : null;
        PlaceCardinals();
        return true;
    }

    private Label CreateCardinal(string text)
    {
        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        label.AddThemeColorOverride("font_color", new Color(.55f, .78f, .94f));
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        _surface.AddChild(label);
        return label;
    }

    private void PlaceCardinals()
    {
        Vector2 size = _surface.Size;
        for (int index = 0; index < _cardinals.Length; index++)
        {
            Label label = _cardinals[index];
            label.Hide();
            if (_cardinalFrame is not { } frame || size.X <= 0 || size.Y <= 0)
                continue;
            Vector3 axis = index % 2 == 0 ? frame.North : frame.East;
            if (index >= 2)
                axis = -axis;
            float depth = axis.Dot(frame.Forward);
            if (depth <= 0)
                continue;
            float scale = size.Y / (2 * depth * _tanHalfField);
            Vector2 point = size / 2 + new Vector2(axis.Dot(frame.Right), -axis.Dot(frame.Up)) * scale;
            if (!new Rect2(Vector2.Zero, size).HasPoint(point))
                continue;
            label.Position = point - new Vector2(label.GetCombinedMinimumSize().X / 2, label.GetCombinedMinimumSize().Y + 3);
            label.Show();
        }
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
