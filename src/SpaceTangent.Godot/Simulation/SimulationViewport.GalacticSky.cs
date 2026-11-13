// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    internal GalacticTextureCache? GalacticCache { get; set; }
    internal bool GalacticSkyVisible => _galacticCanvas is { Visible: true };
    internal Vector3d? GalacticSkyCenter { get; private set; }
    internal bool GroundGalacticSkyVisible => _surface?.GalacticSkyVisible == true;
    private ColorRect? _galacticCanvas;
    private ShaderMaterial? _galacticMaterial;
    private Texture2D? _galacticTexture;
    private bool _hasZodiacalMap;

    private void InstallGalacticSky(CanvasLayer layer, Texture2D? zodiacalMap)
    {
        if (DisplayServer.GetName() == "headless")
            return;
        _galacticMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/galactic_background.gdshader") };
        ConfigureGalacticAxes(_galacticMaterial);
        _hasZodiacalMap = zodiacalMap is not null;
        if (zodiacalMap is not null)
            _galacticMaterial.SetShaderParameter("zodiacal_map", zodiacalMap);
        _galacticCanvas = new() { Visible = false, Material = _galacticMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_galacticCanvas);
        _galacticCanvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    private void PrepareGalacticSky(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        if (_galacticCanvas is null || GalacticCache is not { } cache || !observer.Sky.DeepSky)
        {
            GalacticSkyCenter = null;
            _galacticTexture = null;
            _surface?.SetGalacticSky(null);
            return;
        }
        Vector3d observerPosition = default;
        if (observer.Navigation is { } pose)
        {
            var eye = pose.AnchorKilometers + pose.ResolvePlacement(snapshot, deltaT).OffsetKilometers;
            observerPosition = eye - (CatalogueWorld?.StellarOrigin ?? default);
        }
        var (texture, center) = cache.Request(GalacticLight.Galactocentric(observerPosition / StellarEntry.KilometersPerLightYear));
        if (texture is not null)
        {
            GalacticSkyCenter = center;
            _galacticTexture = texture;
        }
        else if (GalacticSkyCenter is not { } previous || (previous - center).Length > .5)
        {
            GalacticSkyCenter = null;
            _galacticTexture = null;
        }
        _surface?.SetGalacticSky(_galacticTexture);
    }

    private void DrawGalacticSky(bool orbital, Camera3D? camera, float tangent, ObserverView observer,
        WorldSnapshot snapshot, DeltaTModel deltaT, bool survey)
    {
        if (_galacticCanvas is null)
            return;
        var material = _galacticMaterial!;
        bool galactic = _galacticTexture is not null;
        bool zodiacal = false;
        if (_hasZodiacalMap && orbital && camera?.Projection == Camera3D.ProjectionType.Perspective &&
            observer.Navigation is { } pose && snapshot.SolarSystem is { } solar)
        {
            Vector3d sun = (solar.Sun.PositionKilometers - pose.AnchorKilometers) -
                pose.ResolvePlacement(snapshot, deltaT).OffsetKilometers;
            zodiacal = ZodiacalGlow.Configure(material, observer.Sky.DeepSky && observer.Sky.ZodiacalLight && !survey, sun);
        }
        else if (_hasZodiacalMap)
            material.SetShaderParameter("show_zodiacal_light", false);
        _galacticCanvas.Visible = orbital && (galactic || zodiacal) && camera is not null;
        if (!_galacticCanvas.Visible)
            return;
        Basis basis = camera!.GlobalBasis;
        material.SetShaderParameter("show_galactic_image", galactic);
        if (_galacticTexture is not null)
            material.SetShaderParameter("galactic_image", _galacticTexture);
        material.SetShaderParameter("camera_forward", Equatorial(-basis.Z));
        material.SetShaderParameter("camera_right", Equatorial(basis.X));
        material.SetShaderParameter("camera_up", Equatorial(basis.Y));
        material.SetShaderParameter("half_field", tangent);

        static Vector3 Equatorial(Vector3 position) => new(position.X, -position.Z, position.Y);
    }

    internal static void ConfigureGalacticAxes(ShaderMaterial material)
    {
        Basis3d axes = GalacticLight.ToEquatorial;
        material.SetShaderParameter("galactic_x", Convert(axes.X));
        material.SetShaderParameter("galactic_y", Convert(axes.Y));
        material.SetShaderParameter("galactic_z", Convert(axes.Z));

        static Vector3 Convert(Vector3d axis) => new((float)axis.X, (float)axis.Y, (float)axis.Z);
    }
}
