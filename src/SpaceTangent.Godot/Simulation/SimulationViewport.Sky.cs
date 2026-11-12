// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SkyEnvironment = Godot.Environment;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private BrightSkyOverlay? _spaceSky;
    internal SkySurveyCanvas? SpaceSurveyCanvas { get; private set; }
    internal SkySurveyCanvas? SurfaceSurveyCanvas => _surface?.SurveyCanvas;
    private WorldEnvironment? _skyEnvironment;
    private readonly SkyEnvironment _canvasBackground = new()
    {
        BackgroundMode = SkyEnvironment.BGMode.Canvas,
        BackgroundCanvasMaxLayer = -1
    };

    internal void SetSkyData(BrightSkyData data)
    {
        var layer = new CanvasLayer { Layer = -1 };
        AddChild(layer);
        InstallGalacticSky(layer);
        SpaceSurveyCanvas = new(data.Surveys, additive: true);
        layer.AddChild(SpaceSurveyCanvas);
        SpaceSurveyCanvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _spaceSky = new();
        _spaceSky.Configure(data, fillBackground: true);
        layer.AddChild(_spaceSky);
        _spaceSky.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _skyEnvironment = new();
        AddChild(_skyEnvironment);
        _surface?.SetSkyData(data);
    }

    internal bool Draw(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        PrepareGalacticSky(observer, snapshot, deltaT);
        bool available = DrawScene(observer, snapshot, deltaT);
        if (_spaceSky is not null)
        {
            bool localSky = observer.Sky.Stars || observer.Sky.Labels || observer.Sky.EquatorialGrid || observer.Sky.Constellations;
            bool catalogue = localSky && !SpatialStarFieldVisible;
            bool orbital = observer.Mode != CameraMode.Surface || observer.Navigation is not null;
            var camera = GetViewport().GetCamera3D();
            float tangent = camera is null ? 0 : MathF.Tan(Mathf.DegToRad(camera.Fov / 2));
            if (camera?.KeepAspect == Camera3D.KeepAspectEnum.Width && _spaceSky.Size.X > 0)
                tangent *= _spaceSky.Size.Y / _spaceSky.Size.X;
            DrawGalacticSky(orbital, camera, tangent);
            Basis orientation = camera?.GlobalBasis ?? Basis.Identity;
            bool survey = SpaceSurveyCanvas!.ShowFrame(orbital ? observer.Sky.SurveyId : null,
                Icrs(-orientation.Z), Icrs(orientation.X), Icrs(orientation.Y), tangent,
                (float)observer.Sky.EffectiveSurveyBrightness, !GalacticSkyVisible);
            bool culture = observer.Sky.ConstellationArt ||
                (observer.Sky.Constellations && observer.Sky.CultureId is not null);
            bool render = (catalogue || culture || (observer.Sky.Stars && _spaceSky.HasTransientStars)) && orbital;
            _spaceSky.Visible = render;
            _spaceSky.SetFillBackground(!GalacticSkyVisible && !survey);
            SkyEnvironment? background = render || GalacticSkyVisible || survey ? _canvasBackground : null;
            if (_skyEnvironment!.Environment != background)
                _skyEnvironment.Environment = background;
            if (render && camera is not null)
            {
                Basis basis = camera.GlobalBasis;
                Vector3d observerFromSun = observer.Navigation is { } pose && CatalogueWorld is { } world
                    ? pose.AnchorKilometers + pose.ResolvePlacement(snapshot, deltaT).OffsetKilometers - world.StellarOrigin : default;
                _spaceSky.ShowFrame(new(observer.Epoch, observer.Sky, observer.Selection,
                    Icrs(-basis.Z), Icrs(basis.X), Icrs(basis.Y), tangent, ShowCatalogue: catalogue, ObserverFromSun: observerFromSun));
            }
        }
        DrawEyepiece(observer);
        _lastOpticalFrame = observer.ActiveOptics is null ? null : (observer, snapshot, deltaT);
        return available;

        static Vector3 Icrs(Vector3 value) => new(value.X, -value.Z, value.Y);
    }

    internal BrightSkyOverlay? SpaceCultureOverlay => _spaceSky is { Visible: true } ? _spaceSky : null;

    internal bool TransientStarVisible(ObjectId id) => _spaceSky?.TransientVisible(id) == true;

    internal ObjectId? PickObject(Vector2 position) =>
        InsideEyepiece(position) ? PickNavigationBody(position) ?? _surface?.PickStar(position) ?? _spaceSky?.Pick(position) : null;
}
